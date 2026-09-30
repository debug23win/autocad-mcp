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
}
