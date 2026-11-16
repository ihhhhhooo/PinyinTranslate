using System.Globalization;
using System.Text.Json;

namespace PinyinTranslate.Core;

internal sealed class ImageResponseFormatException(Exception? inner = null)
    : Exception("截图识别结果不符合对照格式。", inner);

/// <summary>Normalize equivalent JSON shapes without guessing, dropping, or inventing text pairs.</summary>
internal static class ImageTranslationParser
{
    internal static ImageTranslation Parse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapCodeFence(response));
            JsonElement root = document.RootElement;
            var list = new List<BilingualGroup>();
            if (root.ValueKind == JsonValueKind.Array) ReadGroups(root, list);
            else if (TryField(root, "groups", out var groups)) ReadGroups(groups, list);
            else if (TryField(root, "source", out _) && TryField(root, "translation", out _))
                list.Add(ReadGroup(root, 1));
            else throw new ImageResponseFormatException();

            if (list.Count == 0) throw new InvalidDataException("图片中未识别到可翻译文本，请选择包含清晰文字的区域。");
            // Neither schema conformity nor a second model response proves OCR completeness.
            return new(list, true, "识别结果请与原图核对；模糊、遗漏或阅读顺序可能有误。");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new ImageResponseFormatException(error); }
    }

    private static void ReadGroups(JsonElement groups, List<BilingualGroup> list)
    {
        if (groups.ValueKind != JsonValueKind.Array) throw new ImageResponseFormatException();
        foreach (var item in groups.EnumerateArray()) list.Add(ReadGroup(item, list.Count + 1));
    }

    private static BilingualGroup ReadGroup(JsonElement item, int position)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ImageResponseFormatException();
        string expected = position.ToString(CultureInfo.InvariantCulture);
        string id = expected;
        if (TryField(item, "id", out var field))
        {
            id = field.ValueKind switch
            {
                JsonValueKind.String => field.GetString() ?? "",
                JsonValueKind.Number when field.TryGetInt32(out int value) => value.ToString(CultureInfo.InvariantCulture),
                _ => throw new ImageResponseFormatException()
            };
        }
        if (id != expected) throw new ImageResponseFormatException();
        string source = RequiredText(item, "source");
        string translation = RequiredText(item, "translation");
        bool uncertain = true;
        if (TryField(item, "uncertain", out var uncertainty))
        {
            if (uncertainty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ImageResponseFormatException();
            uncertain = uncertainty.GetBoolean();
        }
        return new(id, source, translation, uncertain);
    }

    private static string RequiredText(JsonElement item, string name)
    {
        if (!TryField(item, name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ImageResponseFormatException();
        return value.GetString()!;
    }

    private static bool TryField(JsonElement item, string name, out JsonElement value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Object) return false;
        bool found = false;
        foreach (var property in item.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw new ImageResponseFormatException(); // Duplicate/case-colliding fields are ambiguous.
            value = property.Value; found = true;
        }
        return found;
    }

    private static string UnwrapCodeFence(string response)
    {
        string json = response.Trim().TrimStart('\uFEFF').Trim();
        if (!json.StartsWith("```", StringComparison.Ordinal)) return json;
        int newline = json.IndexOf('\n');
        if (newline < 0 || !json.EndsWith("```", StringComparison.Ordinal)) throw new ImageResponseFormatException();
        string opening = json[..newline].Trim();
        if (opening != "```" && !opening.Equals("```json", StringComparison.OrdinalIgnoreCase)) throw new ImageResponseFormatException();
        return json[(newline + 1)..^3].Trim();
    }
}
