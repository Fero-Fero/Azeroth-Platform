namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// The raw <c>WTF/Config.wtf</c> settings template for a stack, as edited on the Client tab. The
/// content may use the <c>{{HOST}}</c>/<c>{{PORT}}</c> placeholders, substituted per launcher on serve.
/// </summary>
public sealed class ClientConfigTemplateDto
{
    public string Content { get; set; } = string.Empty;
}
