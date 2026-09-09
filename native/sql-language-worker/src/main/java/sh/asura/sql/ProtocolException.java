package sh.asura.sql;

final class ProtocolException extends Exception {
    private final String code;
    private final boolean fatal;

    ProtocolException(String code, String message) {
        this(code, message, false);
    }

    ProtocolException(String code, String message, boolean fatal) {
        super(message);
        this.code = code;
        this.fatal = fatal;
    }

    String code() {
        return code;
    }

    boolean fatal() {
        return fatal;
    }
}
