import java.io.File;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.Arrays;
import java.util.Optional;

/** Repository-owned fixture reproducing Crash Assistant's executable lookup and child call. */
public final class JvmHostModCompatibility {
    static long pid() { return Long.parseLong(ManagementFactory.getRuntimeMXBean().getName().split("@", 2)[0]); }

    static String currentCommand(String java8Fallback) throws Exception {
        try {
            Class<?> handleClass = Class.forName("java.lang.ProcessHandle");
            Object current = handleClass.getMethod("current").invoke(null);
            Object info = handleClass.getMethod("info").invoke(current);
            Class<?> infoClass = Class.forName("java.lang.ProcessHandle$Info");
            return (String)((Optional<?>)infoClass.getMethod("command").invoke(info)).get();
        } catch (ClassNotFoundException java8) {
            // Linux Java 8 can observe the native executable without a third-party library.
            if (Files.exists(Paths.get("/proc/self/exe"))) return Files.readSymbolicLink(Paths.get("/proc/self/exe")).toString();
            // Windows/macOS Java 8 need CA's platform libraries; this fixture only validates forwarding there.
            return java8Fallback;
        }
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 5) throw new AssertionError("private fixture argument boundary");
        String mode = args[0], host = args[1], java = args[2], scratch = args[3], credential = args[4];
        if (!"JvmHostModCompatibility".equals(System.getProperty("sun.java.command")))
            throw new AssertionError("JNI main class metadata missing or includes game arguments");
        String runtime = System.getenv("NEXACL_JVM_CHILD_JAVA");
        if (runtime == null || !new File(runtime).getCanonicalFile().equals(new File(java).getCanonicalFile())
            || runtime.contains(credential)) throw new AssertionError("selected child Java environment");
        String command = currentCommand(host);
        if (!new File(command).getCanonicalFile().equals(new File(host).getCanonicalFile()))
            throw new AssertionError("native JVM process executable identity");
        ProcessBuilder builder = new ProcessBuilder(command, "-XX:+UseSerialGC", "-Xmx64m", "-Dfixture.child=value with space",
            "-cp", System.getProperty("java.class.path"), "JvmHostModCompatibility$Child",
            mode, "", "中文😀", "literal \"quotes\"", "spaced argument", "$literal;no-shell", scratch, java);
        if (mode.endsWith("orphan")) {
            builder.redirectOutput(new File(scratch, "orphan-stdout.txt"));
            builder.redirectError(new File(scratch, "orphan-stderr.txt"));
        } else builder.inheritIO();
        Process child = builder.start();
        if (mode.endsWith("orphan")) {
            System.out.println("NEXA_CA_PARENT_RETURNED");
            return;
        }
        int exit = child.waitFor();
        if ("exit".equals(mode)) {
            if (exit != 13) throw new AssertionError("child exit was not forwarded");
            System.out.println("NEXA_CA_CHILD_EXIT_13");
            System.exit(7);
        }
        if (exit != 0) throw new AssertionError("Java child failed: " + exit);
        System.out.println("NEXA_CA_PARENT_RETURNED");
    }

    public static final class Child {
        public static void main(String[] args) throws Exception {
            if (args.length != 8 || !args[1].isEmpty() || !args[2].equals("中文😀") || !args[3].equals("literal \"quotes\"")
                || !args[4].equals("spaced argument") || !args[5].equals("$literal;no-shell"))
                throw new AssertionError("child argv roundtrip: " + Arrays.toString(args));
            if (!"value with space".equals(System.getProperty("fixture.child"))) throw new AssertionError("child VM option");
            if (!System.getProperty("sun.java.command", "").startsWith("JvmHostModCompatibility$Child "))
                throw new AssertionError("real Java launcher main metadata");
            String java = args[7];
            if (!new File(currentCommand(java)).getCanonicalFile().equals(new File(java).getCanonicalFile()))
                throw new AssertionError("child is not a real Java executable");
            if (System.getenv("NEXACL_JVM_CHILD_JAVA") != null) throw new AssertionError("bridge marker leaked into real Java");
            Files.write(Paths.get(args[6], "child-pid.txt"), Long.toString(pid()).getBytes(StandardCharsets.UTF_8));
            System.out.println("NEXA_CA_CHILD_STDOUT"); System.err.println("NEXA_CA_CHILD_STDERR");
            if ("exit".equals(args[0])) System.exit(13);
            if ("wait".equals(args[0])) {
                System.out.println("NEXA_CA_CHILD_WAITING");
                Thread.sleep(60000);
            }
            if (args[0].endsWith("orphan")) {
                Thread.sleep("managed-orphan".equals(args[0]) ? 5000 : 1000);
                Files.write(Paths.get(args[6], "child-survived.txt"), "NEXA_CA_CHILD_SURVIVED".getBytes(StandardCharsets.UTF_8));
            }
        }
    }
}
