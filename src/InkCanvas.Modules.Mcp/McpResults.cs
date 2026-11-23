using System.Text;
using System.Text.Json;
using InkCanvas.Host.Contracts;
using ModelContextProtocol.Protocol;

namespace InkCanvas.Modules.Mcp;

public static class McpResults
{
    public static AssistantToolResult FromProtocol(CallToolResult result)
    {
        var text = new StringBuilder(); var images = new List<AssistantToolImage>();
        foreach (var block in result.Content ?? Array.Empty<ContentBlock>())
        {
            switch (block)
            {
                case TextContentBlock content: text.AppendLine(content.Text); break;
                case ImageContentBlock image:
                    images.Add(new(image.DecodedData.ToArray(), image.MimeType)); break;
                case EmbeddedResourceBlock embedded when embedded.Resource is TextResourceContents resource:
                    text.AppendLine(resource.Uri + "\n" + resource.Text); break;
                case EmbeddedResourceBlock embedded when embedded.Resource is BlobResourceContents resource && resource.MimeType?.StartsWith("image/", StringComparison.Ordinal) == true:
                    images.Add(new(resource.DecodedData.ToArray(), resource.MimeType)); break;
                case EmbeddedResourceBlock embedded when embedded.Resource is BlobResourceContents resource:
                    text.AppendLine(resource.Uri + " 的二进制内容当前无法直接读取，类型为 " + resource.MimeType + "。"); break;
                case ResourceLinkBlock link:
                    text.AppendLine(link.Name + "\n" + link.Uri + "\n" + link.Description); break;
                default:
                    using (var json = JsonDocument.Parse(JsonSerializer.Serialize(block, block.GetType())))
                    {
                        if (json.RootElement.TryGetProperty("data", out _) || json.RootElement.TryGetProperty("blob", out _))
                            text.AppendLine("MCP 返回了当前助手无法直接读取的二进制内容。");
                        else text.AppendLine(json.RootElement.GetRawText());
                    }
                    break;
            }
        }
        if (result.StructuredContent is { } structured) text.AppendLine("结构化结果\n" + structured.ToString());
        return new(text.ToString()) { IsError = result.IsError == true, Images = images };
    }

    public static CallToolResult ToProtocol(AssistantToolResult result)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text ?? "" } };
        if (result.Image is { Length: > 0 }) content.Add(ImageContentBlock.FromBytes(result.Image, Mime(result.Image)));
        foreach (var image in result.Images) content.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
        return new() { Content = content, IsError = result.IsError };
    }
    private static string Mime(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 ? "image/jpeg" : "image/png";
}
