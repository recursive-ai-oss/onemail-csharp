// Black-box driver for the released olmreader jar: prints what its public callbacks deliver, as flat "path = value" lines.
// Every bean is dumped by walking its public no-argument getters, so the driver needs no knowledge of the item layouts.
import com.khubla.olmreader.olm.OLMFile;
import com.khubla.olmreader.olm.OLMMessageCallback;
import com.khubla.olmreader.olm.OLMRawMessageCallback;
import com.khubla.olmreader.olm.generated.*;
import java.io.PrintStream;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.*;

public class OlmDump {
    static PrintStream out;
    static int n;

    static void dump(String path, Object o, int depth) throws Exception {
        if (o == null) return;
        if (o instanceof String || o instanceof Number || o instanceof Boolean || o instanceof Enum) {
            out.println(path + " = " + String.valueOf(o).replace("\\", "\\\\").replace("\r", "\\r").replace("\n", "\\n"));
            return;
        }
        if (o instanceof javax.xml.datatype.XMLGregorianCalendar || o instanceof Date) {
            out.println(path + " = " + o);
            return;
        }
        if (o instanceof List) {
            int i = 0;
            for (Object e : (List<?>) o) dump(path + "[" + (i++) + "]", e, depth + 1);
            return;
        }
        if (depth > 12) return;
        Method[] ms = o.getClass().getMethods();
        Arrays.sort(ms, Comparator.comparing(Method::getName));
        for (Method m : ms) {
            String nm = m.getName();
            if (m.getParameterCount() != 0 || !(nm.startsWith("get") || nm.startsWith("is")) || nm.equals("getClass")) continue;
            Object v = m.invoke(o);
            String key = nm.startsWith("get") ? nm.substring(3) : nm.substring(2);
            if (key.equals("Space")) continue;
            dump(path + "." + key, v, depth + 1);
        }
    }

    public static void main(String[] a) throws Exception {
        out = new PrintStream(System.out, false, StandardCharsets.UTF_8);
        OLMFile f = new OLMFile(a[0]);
        f.readOLMFile(new OLMMessageCallback() {
            public void appointment(Appointments.Appointment x) { try { dump("appointment#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
            public void categories(Categories x) { try { dump("categories#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
            public void contact(Contacts.Contact x) { try { dump("contact#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
            public void email(Emails.Email x, HashMap<String, byte[]> att) {
                try {
                    String p = "email#" + n++;
                    dump(p, x, 0);
                    TreeMap<String, byte[]> s = att == null ? new TreeMap<>() : new TreeMap<>(att);
                    for (Map.Entry<String, byte[]> e : s.entrySet()) {
                        byte[] h = MessageDigest.getInstance("SHA-256").digest(e.getValue());
                        out.println(p + ".attachmentFile[" + e.getKey() + "] = " + e.getValue().length + " " + HexFormat.of().formatHex(h));
                    }
                } catch (Exception e) { throw new RuntimeException(e); }
            }
            public void group(Groups.Group x) { try { dump("group#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
            public void note(Notes.Note x) { try { dump("note#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
            public void task(Tasks.Task x) { try { dump("task#" + n++, x, 0); } catch (Exception e) { throw new RuntimeException(e); } }
        }, new OLMRawMessageCallback() { public void rawMessage(String m) { } });
        out.flush();
    }
}
