namespace DbClient.ViewModels;

public sealed record KeyAction(string Id, string Name, string Scope, string Default);

public static class Keymap
{
    public static IReadOnlyList<KeyAction> Actions { get; } =
    [
        new("app.newConnection", "New connection", "Global", "Ctrl+N"),
        new("app.openFile", "Open SQLite file", "Global", "Ctrl+O"),
        new("app.newWindow", "New window", "Global", "Ctrl+Shift+N"),
        new("app.settings", "Settings", "Global", "Ctrl+,"),
        new("app.search", "Search", "Global", "Ctrl+K"),
        new("app.goto", "Go to object", "Global", "Ctrl+P"),
        new("app.closeConnection", "Close connection", "Global", "Ctrl+Shift+W"),
        new("app.toggleSidebar", "Toggle sidebar", "Global", "Ctrl+B"),
        new("tab.newQuery", "New query", "Tabs", "Ctrl+T"),
        new("tab.close", "Close tab", "Tabs", "Ctrl+W"),
        new("editor.run", "Run statement", "Editor", "Ctrl+Enter"),
        new("editor.runAlt", "Run statement (alternate)", "Editor", "F5"),
        new("editor.runScript", "Run script", "Editor", "Ctrl+Shift+Enter"),
        new("editor.complete", "Show suggestions", "Editor", "Ctrl+Space"),
        new("editor.format", "Format SQL", "Editor", "Ctrl+Alt+L"),
        new("editor.comment", "Toggle comment", "Editor", "Ctrl+/"),
        new("editor.duplicate", "Duplicate line", "Editor", "Ctrl+D"),
        new("editor.moveUp", "Move line up", "Editor", "Alt+Shift+Up"),
        new("editor.moveDown", "Move line down", "Editor", "Alt+Shift+Down"),
        new("editor.save", "Save query", "Editor", "Ctrl+S"),
        new("editor.saveAs", "Save query as", "Editor", "Ctrl+Shift+S"),
        new("editor.commit", "Commit transaction", "Editor", "Ctrl+Alt+Enter"),
        new("editor.rollback", "Roll back transaction", "Editor", "Ctrl+Alt+Back"),
        new("grid.save", "Commit edits", "Table", "Ctrl+S"),
        new("grid.refresh", "Refresh", "Table", "F5"),
        new("grid.addRow", "Add row", "Table", "Ctrl+I"),
        new("grid.delete", "Delete rows", "Table", "Delete"),
        new("grid.setNull", "Set to NULL", "Table", "Ctrl+0"),
        new("grid.recordView", "Record view", "Table", "Ctrl+Shift+R"),
        new("grid.copyHeaders", "Copy with headers", "Table", "Ctrl+Shift+C"),
        new("grid.columns", "Column list", "Table", "Ctrl+Shift+L"),
    ];

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Presets { get; } = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["Qorra"] = new Dictionary<string, string>(),
        ["DataGrip"] = new Dictionary<string, string>
        {
            ["editor.moveUp"] = "Ctrl+Shift+Up", ["editor.moveDown"] = "Ctrl+Shift+Down", ["app.settings"] = "Ctrl+Alt+S",
            ["app.goto"] = "Ctrl+Shift+A", ["editor.runAlt"] = "",
        },
        ["VS Code"] = new Dictionary<string, string>
        {
            ["editor.format"] = "Shift+Alt+F", ["editor.moveUp"] = "Alt+Up", ["editor.moveDown"] = "Alt+Down",
            ["editor.duplicate"] = "Shift+Alt+Down", ["app.search"] = "Ctrl+Shift+P", ["editor.runAlt"] = "",
        },
        ["SSMS"] = new Dictionary<string, string>
        {
            ["editor.run"] = "Ctrl+E", ["editor.runAlt"] = "", ["editor.runScript"] = "F5", ["editor.comment"] = "Ctrl+Shift+/",
            ["grid.refresh"] = "Ctrl+R",
        },
    };
}
