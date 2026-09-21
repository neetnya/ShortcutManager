using System.Text.Json.Serialization;

namespace ShortcutManager.Models;

/// <summary>一个分组，对应界面上一个 tab。</summary>
public sealed class ShortcutGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "新分组";

    public List<ShortcutItem> Items { get; set; } = new();
}
