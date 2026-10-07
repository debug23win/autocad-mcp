namespace CadMcp.Core;

// Evaluator approach adapted from felixalmesberger/AUTOCAD-MCP AcadExecutor.cs (MIT).
// Copyright (c) 2026 Felix Almesberger. See licenses/felixalmesberger-MIT.txt.
// Changes: managed callbacks, local variables, no temp files, validated document at start, explicit status and undo group.
public static class LispScript
{
    public static string Wrap(string id)
    {
        if (id.Length is < 1 or > 96 || id.Any(c => !Portable.AsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Invalid operation id");
        return "((lambda (/ code value undo-start undo-end) " +
            $"(setq code (cadmcpbegin \"{id}\")) " +
            "(if code (progn " +
            "(setq undo-start (vl-catch-all-apply 'command-s '(\"_.UNDO\" \"_Begin\"))) " +
            "(if (vl-catch-all-error-p undo-start) " +
            $"(cadmcpfinish \"{id}\" 0 (vl-catch-all-error-message undo-start)) " +
            "(progn " +
            "(setq value (vl-catch-all-apply (function (lambda () (eval (read code)))))) " +
            "(setq undo-end (vl-catch-all-apply 'command-s '(\"_.UNDO\" \"_End\"))) " +
            $"(cadmcpfinish \"{id}\" " +
            "(if (or (vl-catch-all-error-p value) (vl-catch-all-error-p undo-end)) 0 1) " +
            "(cond ((vl-catch-all-error-p value) (vl-catch-all-error-message value)) " +
            "((vl-catch-all-error-p undo-end) (vl-catch-all-error-message undo-end)) " +
            "(T (vl-prin1-to-string value)))))))) (princ)))\n";
    }

    /// <summary>
    /// The body is evaluated as <c>(eval (read "(progn body)"))</c>, and <c>read</c> takes only the first
    /// expression. Reject bodies whose parentheses do not balance, so that code after a stray ")" is not
    /// silently dropped while the operation reports success.
    /// </summary>
    public static void ValidateBody(string code)
    {
        int depth = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '"')
            {
                for (i++; i < code.Length && code[i] != '"'; i++) if (code[i] == '\\') i++;
                if (i >= code.Length) throw new CadFault("LISP_UNBALANCED", "AutoLISP string is not terminated");
            }
            else if (c == ';' && i + 1 < code.Length && code[i + 1] == '|')
            {
                int end = code.IndexOf("|;", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new CadFault("LISP_UNBALANCED", "AutoLISP block comment ;| ... |; is not terminated");
                i = end + 1;
            }
            else if (c == ';') { while (i < code.Length && code[i] != '\n') i++; }
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0)
                throw new CadFault("LISP_UNBALANCED", "AutoLISP has an extra ')' at character " + (i + 1) + "; code after it would not run");
        }
        if (depth != 0) throw new CadFault("LISP_UNBALANCED", "AutoLISP is missing " + depth + " closing parenthesis(es)");
    }
}
