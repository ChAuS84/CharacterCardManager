using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CharacterCardManager.Models;

namespace CharacterCardManager.Services;

public static partial class PngCardCodec
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [GeneratedRegex(@"-(\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex BotbooruFilenameIdRegex();

    public static CharacterCardData ReadCard(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        byte[] bytes = File.ReadAllBytes(filePath);

        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException($"Not a valid PNG file: {filePath}");

        string? charaJson = null;
        string? ccv3Json = null;

        int pos = 8;
        while (pos + 12 <= bytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            if (pos + 12L + len > bytes.Length)
                break;

            string type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (type == "tEXt" && len > 0)
            {
                ReadOnlySpan<byte> payload = bytes.AsSpan(pos + 8, (int)len);
                int nullIdx = payload.IndexOf((byte)0);
                if (nullIdx > 0)
                {
                    string keyword = Encoding.Latin1.GetString(payload[..nullIdx]);
                    if (keyword is "chara" or "ccv3")
                    {
                        string b64 = Encoding.ASCII.GetString(payload[(nullIdx + 1)..]);
                        byte[] decoded = Convert.FromBase64String(b64);
                        string json = Encoding.UTF8.GetString(decoded);
                        if (keyword == "ccv3")
                            ccv3Json = json;
                        else
                            charaJson = json;
                    }
                }
            }

            pos += 12 + (int)len;
            if (type == "IEND")
                break;
        }

        string? primaryJson = ccv3Json ?? charaJson;
        if (string.IsNullOrWhiteSpace(primaryJson))
            throw new InvalidDataException($"No Tavern character metadata (chara/ccv3) found in {fileInfo.Name}");

        var root = JsonNode.Parse(primaryJson)?.AsObject()
            ?? throw new InvalidDataException($"Invalid JSON metadata in {fileInfo.Name}");

        string spec = root["spec"]?.GetValue<string>() ?? string.Empty;
        JsonObject dataObj;
        string specVersion;

        if (spec == "chara_card_v3" && root["data"] is JsonObject v3Data)
        {
            dataObj = v3Data;
            specVersion = "v3";
        }
        else if (spec == "chara_card_v2" && root["data"] is JsonObject v2Data)
        {
            dataObj = v2Data;
            specVersion = "v2";
        }
        else if (root["data"] is JsonObject fallbackData)
        {
            dataObj = fallbackData;
            specVersion = string.IsNullOrEmpty(spec) ? "v2" : spec;
        }
        else
        {
            dataObj = root;
            specVersion = "v1";
        }

        var card = new CharacterCardData
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
            SpecVersion = specVersion,
            HasCcv3Chunk = ccv3Json is not null,
            RawRoot = root,
            Name = GetStr(dataObj, "name"),
            Creator = GetStr(dataObj, "creator"),
            CharacterVersion = GetStr(dataObj, "character_version"),
            Tagline = GetStr(dataObj, "tagline"),
            Nickname = GetStr(dataObj, "nickname"),
            Description = GetStr(dataObj, "description"),
            Personality = GetStr(dataObj, "personality"),
            Scenario = GetStr(dataObj, "scenario"),
            FirstMessage = GetStr(dataObj, "first_mes"),
            MessageExample = GetStr(dataObj, "mes_example"),
            CreatorNotes = GetStr(dataObj, "creator_notes"),
            SystemPrompt = GetStr(dataObj, "system_prompt"),
            PostHistoryInstructions = GetStr(dataObj, "post_history_instructions"),
            AlternateGreetings = GetStringArray(dataObj["alternate_greetings"]),
            Tags = GetStringArray(dataObj["tags"]),
            LorebookCount = CountLorebookEntries(dataObj["character_book"]),
            BotbooruPostId = ExtractBotbooruPostId(dataObj, fileInfo.Name)
        };

        return card;
    }

    public static void WriteCard(CharacterCardData card, byte[]? overrideImagePngBytes = null)
    {
        var root = card.RawRoot?.DeepClone().AsObject() ?? new JsonObject();
        JsonObject dataObj;

        if (card.SpecVersion == "v1" && root["data"] is null)
        {
            dataObj = root;
        }
        else
        {
            if (root["data"] is not JsonObject existingData)
            {
                existingData = new JsonObject();
                root["data"] = existingData;
                if (root["spec"] is null)
                {
                    root["spec"] = card.SpecVersion == "v3" ? "chara_card_v3" : "chara_card_v2";
                    root["spec_version"] = card.SpecVersion == "v3" ? "3.0" : "2.0";
                }
            }
            dataObj = existingData;
        }

        dataObj["name"] = card.Name;
        dataObj["creator"] = card.Creator;
        dataObj["character_version"] = card.CharacterVersion;
        dataObj["tagline"] = card.Tagline;
        dataObj["nickname"] = card.Nickname;
        dataObj["description"] = card.Description;
        dataObj["personality"] = card.Personality;
        dataObj["scenario"] = card.Scenario;
        dataObj["first_mes"] = card.FirstMessage;
        dataObj["mes_example"] = card.MessageExample;
        dataObj["creator_notes"] = card.CreatorNotes;
        dataObj["system_prompt"] = card.SystemPrompt;
        dataObj["post_history_instructions"] = card.PostHistoryInstructions;

        var altArray = new JsonArray();
        foreach (var g in card.AlternateGreetings)
            altArray.Add(g);
        dataObj["alternate_greetings"] = altArray;

        var tagArray = new JsonArray();
        foreach (var t in card.Tags)
            tagArray.Add(t);
        dataObj["tags"] = tagArray;

        if (card.SpecVersion == "v3")
            dataObj["modification_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string updatedJson = root.ToJsonString(WriteJsonOptions);
        string updatedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedJson));

        byte[] sourceBytes = overrideImagePngBytes ?? File.ReadAllBytes(card.FilePath);
        if (sourceBytes.Length < 8 || !sourceBytes.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException($"Not a valid PNG file: {card.FilePath}");

        using var output = new MemoryStream(sourceBytes.Length + 4096);
        output.Write(PngSignature);

        bool wroteChara = false;
        bool wroteCcv3 = false;
        bool needCcv3 = card.HasCcv3Chunk || card.SpecVersion == "v3";

        int pos = 8;
        while (pos + 12 <= sourceBytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(pos, 4));
            if (pos + 12L + len > sourceBytes.Length)
                break;

            string type = Encoding.ASCII.GetString(sourceBytes, pos + 4, 4);
            ReadOnlySpan<byte> payload = sourceBytes.AsSpan(pos + 8, (int)len);

            if (type == "tEXt")
            {
                int nullIdx = payload.IndexOf((byte)0);
                string keyword = nullIdx > 0 ? Encoding.Latin1.GetString(payload[..nullIdx]) : string.Empty;
                if (keyword == "chara")
                {
                    WriteTextChunk(output, "chara", updatedBase64);
                    wroteChara = true;
                    pos += 12 + (int)len;
                    continue;
                }
                if (keyword == "ccv3")
                {
                    if (needCcv3)
                    {
                        WriteTextChunk(output, "ccv3", updatedBase64);
                        wroteCcv3 = true;
                    }
                    pos += 12 + (int)len;
                    continue;
                }
            }

            if (type == "IEND")
            {
                if (!wroteChara)
                    WriteTextChunk(output, "chara", updatedBase64);
                if (needCcv3 && !wroteCcv3)
                    WriteTextChunk(output, "ccv3", updatedBase64);
            }

            // Copy original chunk verbatim (len + type + payload + crc)
            output.Write(sourceBytes, pos, 12 + (int)len);
            pos += 12 + (int)len;
            if (type == "IEND")
                break;
        }

        string tempPath = card.FilePath + ".tmp";
        File.WriteAllBytes(tempPath, output.ToArray());
        File.Move(tempPath, card.FilePath, overwrite: true);

        var fi = new FileInfo(card.FilePath);
        card.FileSize = fi.Length;
        card.LastModifiedTicks = fi.LastWriteTimeUtc.Ticks;
        card.RawRoot = root;
    }

    private static void WriteTextChunk(Stream output, string keyword, string text)
    {
        byte[] typeBytes = "tEXt"u8.ToArray();
        byte[] keyBytes = Encoding.Latin1.GetBytes(keyword);
        byte[] textBytes = Encoding.Latin1.GetBytes(text);

        int payloadLen = keyBytes.Length + 1 + textBytes.Length;
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)payloadLen);

        byte[] crcInput = new byte[4 + payloadLen];
        typeBytes.CopyTo(crcInput, 0);
        keyBytes.CopyTo(crcInput, 4);
        crcInput[4 + keyBytes.Length] = 0;
        textBytes.CopyTo(crcInput, 4 + keyBytes.Length + 1);

        uint crc = ComputeCrc32(crcInput);
        byte[] crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);

        output.Write(lenBytes);
        output.Write(crcInput);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static string GetStr(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue val && val.TryGetValue<string>(out var s))
            return s;
        return string.Empty;
    }

    private static List<string> GetStringArray(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }
        return list;
    }

    private static int CountLorebookEntries(JsonNode? node)
    {
        if (node is JsonObject obj && obj["entries"] is JsonArray arr)
            return arr.Count;
        return 0;
    }

    private static long? ExtractBotbooruPostId(JsonObject dataObj, string fileName)
    {
        if (dataObj["extensions"] is JsonObject ext &&
            ext["botbooru"] is JsonObject bb &&
            bb["post_id"] is JsonValue pidVal)
        {
            if (pidVal.TryGetValue<long>(out var pid))
                return pid;
            if (pidVal.TryGetValue<int>(out var pidInt))
                return pidInt;
        }

        var m = BotbooruFilenameIdRegex().Match(fileName);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var fromName))
            return fromName;

        return null;
    }
}
