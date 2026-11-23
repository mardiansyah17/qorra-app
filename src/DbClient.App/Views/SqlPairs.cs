using ICSharpCode.AvalonEdit;

namespace DbClient.App.Views;

internal static class SqlPairs
{
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    public static bool StepOver(TextEditor editor, string typed)
    {
        var caret = editor.CaretOffset;
        if (typed is not (")" or "'" or "\"") || editor.SelectionLength > 0 || caret >= editor.Document.TextLength
            || editor.Document.GetCharAt(caret) != typed[0]) return false;
        editor.CaretOffset = caret + 1;
        return true;
    }

    public static void AutoClose(TextEditor editor, char c)
    {
        if (c is not ('(' or '\'' or '"')) return;
        var doc = editor.Document;
        var caret = editor.CaretOffset;
        var next = caret < doc.TextLength ? doc.GetCharAt(caret) : ' ';
        if (!(char.IsWhiteSpace(next) || next is ')' or ',' or ';')) return;
        if (c != '(' && caret >= 2 && IsWordChar(doc.GetCharAt(caret - 2))) return;
        doc.Insert(caret, c == '(' ? ")" : c.ToString());
        editor.CaretOffset = caret;
    }

    public static bool DeletePair(TextEditor editor)
    {
        var caret = editor.CaretOffset;
        var doc = editor.Document;
        if (editor.SelectionLength > 0 || caret == 0 || caret >= doc.TextLength) return false;
        if ((doc.GetCharAt(caret - 1), doc.GetCharAt(caret)) is not (('(', ')') or ('\'', '\'') or ('"', '"'))) return false;
        doc.Remove(caret - 1, 2);
        return true;
    }
}
