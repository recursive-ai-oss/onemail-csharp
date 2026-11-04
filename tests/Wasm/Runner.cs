using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace RecursiveAI.OneMail.Tests.WasmHost;

/// <summary>A small test runner for the browser. xUnit's own runner needs threads and a console host that the browser does not
/// give a .NET app, so this one discovers the same [Fact]/[Theory] methods by reflection (fine in a test host) and runs them one
/// after another on the single browser thread. It prints failures as they happen and a final line with the totals.
/// A test or class marked <c>[Trait("Platform", "native")]</c> needs something the browser lacks (the reason is the trait's second
/// word, e.g. "native" with a <c>Reason</c> trait) and is counted as an exclusion, not run.</summary>
internal static class Runner
{
    private const string DynamicSkipPrefix = "$XunitDynamicSkip$";

    private static async Task<int> Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : "";
        int passed = 0, failed = 0, skipped = 0;
        var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        Stopwatch total = Stopwatch.StartNew();

        Type[] types = typeof(Runner).Assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && t is { IsAbstract: false, IsGenericTypeDefinition: false } || (t.IsPublic && t.IsAbstract && t.IsSealed))
            .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        foreach (Type type in types)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<FactAttribute>(true).Any()).ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            string typeName = type.FullName ?? type.Name;
            if (filter.Length > 0 && !typeName.Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            string? classExclusion = ExclusionOf(type.GetCustomAttributes<TraitAttribute>(true));
            Stopwatch classTime = Stopwatch.StartNew();
            int classFailed = 0;
            foreach (MethodInfo method in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                string name = typeName + "." + method.Name;
                string? exclusion = classExclusion ?? ExclusionOf(method.GetCustomAttributes<TraitAttribute>(true));
                FactAttribute fact = method.GetCustomAttributes<FactAttribute>(true).First();
                if (exclusion is not null)
                {
                    excluded[exclusion] = excluded.GetValueOrDefault(exclusion) + CountRows(method, fact);
                    continue;
                }

                if (fact.Skip is { } skip)
                {
                    skipped++;
                    skipReasons[skip] = skipReasons.GetValueOrDefault(skip) + 1;
                    continue;
                }

                var rows = new List<(string Label, object?[] Args, string? Skip)>();
                if (method.GetCustomAttributes(true).OfType<IDataAttribute>().Any() || fact is TheoryAttribute)
                {
                    try
                    {
                        foreach (IDataAttribute data in method.GetCustomAttributes(true).OfType<IDataAttribute>())
                        {
                            foreach (ITheoryDataRow row in await data.GetData(method, new DisposalTracker()))
                            {
                                rows.Add((Describe(row.GetData()), row.GetData(), row.Skip ?? data.Skip));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        classFailed++;
                        failures.Add(name + " [data]: " + Short(e));
                        continue;
                    }
                }
                else
                {
                    rows.Add(("", [], null));
                }

                foreach ((string label, object?[] rowArgs, string? rowSkip) in rows)
                {
                    if (rowSkip is not null)
                    {
                        skipped++;
                        skipReasons[rowSkip] = skipReasons.GetValueOrDefault(rowSkip) + 1;
                        continue;
                    }

                    (string outcome, string? detail) = await RunOne(type, method, rowArgs);
                    switch (outcome)
                    {
                        case "pass":
                            passed++;
                            break;
                        case "skip":
                            skipped++;
                            skipReasons[detail ?? ""] = skipReasons.GetValueOrDefault(detail ?? "") + 1;
                            break;
                        default:
                            failed++;
                            classFailed++;
                            failures.Add(name + label + ": " + detail);
                            Console.WriteLine("FAIL " + name + label + ": " + detail);
                            break;
                    }
                }
            }

            Console.WriteLine($"{(classFailed == 0 ? "ok  " : "FAIL")} {typeName} {classTime.ElapsedMilliseconds} ms");
        }

        Console.WriteLine();
        foreach ((string reason, int n) in skipReasons)
        {
            Console.WriteLine($"skipped x{n}: {reason}");
        }

        foreach ((string reason, int n) in excluded)
        {
            Console.WriteLine($"excluded x{n}: {reason}");
        }

        int excludedTotal = excluded.Values.Sum();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"wasm tests: total={passed + failed + skipped + excludedTotal} run={passed + failed} passed={passed} failed={failed} skipped={skipped} excluded={excludedTotal} in {total.Elapsed.TotalSeconds:F1} s"));
        return failed == 0 ? 0 : 1;
    }

    private static string? ExclusionOf(IEnumerable<TraitAttribute> traits)
    {
        string? platform = null, reason = null;
        foreach (TraitAttribute t in traits)
        {
            if (t.Name == "Platform" && t.Value == "native")
            {
                platform = "native";
            }
            else if (t.Name == "Reason")
            {
                reason = t.Value;
            }
        }

        return platform is null ? null : reason ?? "needs a native platform";
    }

    private static int CountRows(MethodInfo method, FactAttribute fact)
    {
        if (fact is not TheoryAttribute)
        {
            return 1;
        }

        try
        {
            int n = 0;
            foreach (IDataAttribute data in method.GetCustomAttributes(true).OfType<IDataAttribute>())
            {
                n += data.GetData(method, new DisposalTracker()).AsTask().GetAwaiter().GetResult().Count;
            }

            return Math.Max(n, 1);
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task<(string Outcome, string? Detail)> RunOne(Type type, MethodInfo method, object?[] args)
    {
        object? instance = null;
        try
        {
            if (!method.IsStatic)
            {
                instance = Activator.CreateInstance(type);
                if (instance is IAsyncLifetime life)
                {
                    await life.InitializeAsync();
                }
            }

            object? result = method.Invoke(instance, Coerce(method, args));
            switch (result)
            {
                case Task t:
                    await t;
                    break;
                case ValueTask vt:
                    await vt;
                    break;
            }

            return ("pass", null);
        }
        catch (Exception e)
        {
            Exception inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
            if (inner.Message.StartsWith(DynamicSkipPrefix, StringComparison.Ordinal))
            {
                return ("skip", inner.Message[DynamicSkipPrefix.Length..]);
            }

            return ("fail", Short(inner));
        }
        finally
        {
            try
            {
                if (instance is IAsyncDisposable ad)
                {
                    await ad.DisposeAsync();
                }
                else if (instance is IDisposable d)
                {
                    d.Dispose();
                }
            }
            catch (Exception)
            {
                // A failing Dispose after a passing test is reported by the native run; here it only must not stop the sweep.
            }
        }
    }

    // xUnit converts theory data to the parameter types (an int literal for a byte parameter, a string for an enum).
    private static object?[] Coerce(MethodInfo method, object?[] args)
    {
        ParameterInfo[] ps = method.GetParameters();
        var result = (object?[])args.Clone();
        for (int i = 0; i < result.Length && i < ps.Length; i++)
        {
            Type t = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
            if (result[i] is { } v && !t.IsInstanceOfType(v) && !t.IsInterface && t != typeof(object))
            {
                result[i] = t.IsEnum ? Enum.ToObject(t, v) : Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }

    private static string Describe(object?[] args)
    {
        var sb = new StringBuilder("(");
        for (int i = 0; i < args.Length; i++)
        {
            string s = Convert.ToString(args[i], CultureInfo.InvariantCulture) ?? "null";
            sb.Append(i > 0 ? ", " : "").Append(s.Length > 60 ? s[..60] + "..." : s);
        }

        return sb.Append(')').ToString().Replace('\n', ' ').Replace('\r', ' ');
    }

    private static string Short(Exception e)
    {
        string m = e.GetType().Name + ": " + e.Message;
        m = m.Length > 600 ? m[..600] + "..." : m;
        string? at = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("OneMail", StringComparison.Ordinal));
        return (m + " @" + at?.Trim()).Replace('\n', ' ').Replace('\r', ' ');
    }
}
