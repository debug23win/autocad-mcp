namespace CadMcp.Core;

// Evaluator approach adapted from felixalmesberger/AUTOCAD-MCP AcadExecutor.cs (MIT).
// Copyright (c) 2026 Felix Almesberger. See licenses/felixalmesberger-MIT.txt.
// Changes: managed callbacks, local variables, no temp files, validated document at start, explicit status and undo group.
public static class LispScript
{
    public static string Wrap(string id)
    {
        if (id.Length is < 1 or > 96 || id.Any(c => !Portable.AsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Invalid operation id");
        // While the script runs, running object snaps are suspended (OSMODE bit 16384), so command points land
        // exactly on the supplied coordinates, and FILEDIA/CMDDIA are off, so commands prompt on the command
        // line instead of waiting in a dialog. Afterwards each variable gets the user's value back, unless the script
        // completed and set that variable itself. AutoLISP locals are dynamically scoped, so the script sees the wrapper's
        // locals: their prefixed names keep a script's own (setq saved ...) from breaking the restore, and the
        // restore runs under vl-catch-all-apply, so cadmcpfinish is always reached.
        return "((lambda (/ cadmcp:code cadmcp:value cadmcp:undo-start cadmcp:undo-end cadmcp:saved cadmcp:set cadmcp:item) " +
            $"(setq cadmcp:code (cadmcpbegin \"{id}\")) " +
            "(if cadmcp:code (progn " +
            "(setq cadmcp:undo-start (vl-catch-all-apply 'command-s '(\"_.UNDO\" \"_Begin\"))) " +
            "(if (vl-catch-all-error-p cadmcp:undo-start) " +
            $"(cadmcpfinish \"{id}\" 0 (vl-catch-all-error-message cadmcp:undo-start)) " +
            "(progn " +
            "(setq cadmcp:saved (mapcar '(lambda (cadmcp:name) (cons cadmcp:name (getvar cadmcp:name))) '(\"OSMODE\" \"FILEDIA\" \"CMDDIA\"))) " +
            "(setq cadmcp:set (list (cons \"OSMODE\" (logior (cdr (assoc \"OSMODE\" cadmcp:saved)) 16384)) '(\"FILEDIA\" . 0) '(\"CMDDIA\" . 0))) " +
            "(foreach cadmcp:item cadmcp:set (vl-catch-all-apply 'setvar (list (car cadmcp:item) (cdr cadmcp:item)))) " +
            "(setq cadmcp:value (vl-catch-all-apply (function (lambda () (eval (read cadmcp:code)))))) " +
            "(vl-catch-all-apply (function (lambda () (foreach cadmcp:item cadmcp:saved " +
            "(if (or (vl-catch-all-error-p cadmcp:value) (equal (getvar (car cadmcp:item)) (cdr (assoc (car cadmcp:item) cadmcp:set)))) " +
            "(vl-catch-all-apply 'setvar (list (car cadmcp:item) (cdr cadmcp:item)))))))) " +
            "(setq cadmcp:undo-end (vl-catch-all-apply 'command-s '(\"_.UNDO\" \"_End\"))) " +
            $"(cadmcpfinish \"{id}\" " +
            "(if (or (vl-catch-all-error-p cadmcp:value) (vl-catch-all-error-p cadmcp:undo-end)) 0 1) " +
            "(cond ((vl-catch-all-error-p cadmcp:value) (vl-catch-all-error-message cadmcp:value)) " +
            "((vl-catch-all-error-p cadmcp:undo-end) (vl-catch-all-error-message cadmcp:undo-end)) " +
            "(T (vl-prin1-to-string cadmcp:value)))))))) (princ)))\n";
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
