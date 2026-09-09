package sh.asura.sql;

/** Process entry point. Standard output is reserved exclusively for framed responses. */
public final class Main {
    private Main() {
    }

    public static void main(String[] args) {
        var worker = new SqlLanguageWorker();
        try {
            int exitCode = worker.run(System.in, System.out);
            if (exitCode != 0) {
                System.exit(exitCode);
            }
        } catch (Exception error) {
            System.err.println("Asura SQL language worker stopped: " + safeMessage(error));
            System.exit(1);
        }
    }

    private static String safeMessage(Throwable error) {
        var message = error.getMessage();
        return message == null || message.isBlank()
            ? error.getClass().getSimpleName()
            : message;
    }
}
