using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail.Tests;

public class PackageLayoutTests
{
    // Package ID, assembly name and namespace are all the prefix plus the project name; the build derives them in one place.
    [Theory]
    [InlineData(typeof(CalendarLibrary), CalendarLibrary.Name)]
    [InlineData(typeof(OneMailLibrary), OneMailLibrary.Name)]
    [InlineData(typeof(ClientLibrary), ClientLibrary.Name)]
    [InlineData(typeof(CryptographyLibrary), CryptographyLibrary.Name)]
    [InlineData(typeof(RenderLibrary), RenderLibrary.Name)]
    public void AssemblyNameIsThePackageId(Type marker, string name)
    {
        Assert.Equal(name, marker.Assembly.GetName().Name);
        Assert.StartsWith(name, marker.Namespace, StringComparison.Ordinal);
    }

    [Fact]
    public void CorePackageIsBuiltOnCalendar()
    {
        Assert.Equal("RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)", OneMailLibrary.Describe());
    }
}
