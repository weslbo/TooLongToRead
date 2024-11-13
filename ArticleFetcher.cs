using System.ComponentModel;
using Microsoft.SemanticKernel;
using System.Net.Http;
using ReverseMarkdown;

public class ArticleFetcher
{
    [KernelFunction("get_article")]
    [Description("Retrieve the content of an webpage, by it's URL")]
    [return: Description("The content of the article")]
    public async Task<string> GetArticle([Description("The URL of the article/web page")]string url)
    {
        try
        {
            var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();

            var htmlDocument = new HtmlAgilityPack.HtmlDocument();
            htmlDocument.LoadHtml(content);
            var bodyNode = htmlDocument.DocumentNode.SelectSingleNode("//body");
            var bodyText = bodyNode?.InnerText ?? string.Empty;

            var converter = new ReverseMarkdown.Converter();
            var markdown = converter.Convert(bodyText);
            var markdownoptimized = System.Text.RegularExpressions.Regex.Replace(markdown.ToString(), @"(\[.*?\]\(.*?\))|(```.*?```)|(`.*?`)|(\*\*.*?\*\*)|(\*.*?\*)|(_.*?_)|(~.*?~)", " ");

            return markdownoptimized;
        }
        catch
        {
            return string.Empty;
        }
    }
}