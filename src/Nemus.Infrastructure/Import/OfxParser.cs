using System.Globalization;
using System.Text;
using Nemus.Domain.Primitives;

namespace Nemus.Infrastructure.Import;

/// <summary>
/// Le OFX 1.x (SGML) e 2.x (XML) com o mesmo codigo.
///
/// CODIFICACAO. O cabecalho do OFX 1.x traz ENCODING e CHARSET, e banco
/// brasileiro costuma mandar CHARSET:1252 (Windows-1252). O .NET moderno
/// nao carrega essas paginas de codigo por padrao: sem registrar o provider,
/// "PADARIA SAO JOAO" chega como "PADARIA S?O JO?O" e o nome do
/// estabelecimento fica corrompido para sempre no banco de dados. Por isso
/// o cabecalho e lido em ASCII primeiro, so para descobrir a codificacao
/// real, e o corpo e decodificado depois.
/// </summary>
public static class OfxParser
{
    private static bool _codePagesRegistered;

    public static Result<OfxNode> Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    public static Result<OfxNode> Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            return new Error("ofx.empty", "Arquivo vazio.");
        }

        Encoding encoding = DetectEncoding(content);
        string text = encoding.GetString(content);

        // Remove BOM residual: alguns bancos mandam BOM mesmo declarando
        // USASCII, e o caractere invisivel derruba a busca por "<OFX>".
        text = text.TrimStart('﻿');

        int start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return new Error("ofx.not_ofx", "O arquivo nao parece ser um OFX: nao ha elemento OFX.");
        }

        OfxNode root = Tokenize(text.AsSpan(start));

        if (root.Children.Count == 0)
        {
            return new Error("ofx.malformed", "O documento OFX nao tem conteudo legivel.");
        }

        return root;
    }

    /// <summary>
    /// Tokenizador tolerante. A regra que faz os dois formatos caberem:
    /// uma tag seguida de texto e FOLHA e ja se fecha sozinha; uma tag
    /// seguida de outra tag e AGREGACAO e fica aberta.
    ///
    /// Em XML isso significa que a tag de fechamento de uma folha chega
    /// quando ela ja foi fechada - dai o fechamento sem par correspondente
    /// ser ignorado em vez de virar erro.
    /// </summary>
    private static OfxNode Tokenize(ReadOnlySpan<char> text)
    {
        var root = new OfxNode("#root");
        var stack = new Stack<OfxNode>();
        stack.Push(root);

        int i = 0;
        while (i < text.Length)
        {
            int open = text[i..].IndexOf('<');
            if (open < 0)
            {
                break;
            }

            open += i;
            int close = text[open..].IndexOf('>');
            if (close < 0)
            {
                break;
            }

            close += open;
            ReadOnlySpan<char> tag = text[(open + 1)..close].Trim();
            i = close + 1;

            if (tag.IsEmpty)
            {
                continue;
            }

            // Declaracao de processamento do OFX 2.x: <?xml ...?> e <?OFX ...?>
            if (tag[0] == '?' || tag[0] == '!')
            {
                continue;
            }

            if (tag[0] == '/')
            {
                CloseTag(stack, tag[1..].Trim());
                continue;
            }

            // Tag vazia no estilo XML: <TAG/>
            bool selfClosing = tag[^1] == '/';
            if (selfClosing)
            {
                tag = tag[..^1].Trim();
            }

            var node = new OfxNode(tag.ToString());
            stack.Peek().Add(node);

            if (selfClosing)
            {
                continue;
            }

            // O que vem depois do '>' decide se e folha ou agregacao.
            int next = text[i..].IndexOf('<');
            ReadOnlySpan<char> between = next < 0 ? text[i..] : text.Slice(i, next);

            if (!between.IsWhiteSpace())
            {
                node.Value = Unescape(between.Trim());
                // Folha: ja esta completa, nao entra na pilha.
            }
            else
            {
                stack.Push(node);
            }
        }

        return root;
    }

    /// <summary>
    /// Desempilha ate encontrar a tag correspondente. Se nao houver
    /// correspondencia - caso do fechamento de folha em XML, ja tratada -
    /// a pilha fica intacta.
    /// </summary>
    private static void CloseTag(Stack<OfxNode> stack, ReadOnlySpan<char> name)
    {
        string target = name.ToString();

        bool isOpen = stack.Any(n => string.Equals(n.Name, target, StringComparison.OrdinalIgnoreCase));
        if (!isOpen)
        {
            return;
        }

        while (stack.Count > 1)
        {
            OfxNode closed = stack.Pop();
            if (string.Equals(closed.Name, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Entidades XML aparecem em OFX 2.x e, ocasionalmente, em 1.x.
    /// &amp;amp; precisa ser o ultimo a ser trocado, senao "&amp;amp;lt;"
    /// viraria "&lt;".
    /// </summary>
    private static string Unescape(ReadOnlySpan<char> value)
    {
        string text = value.ToString();

        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        return text
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding DetectEncoding(byte[] content)
    {
        if (!_codePagesRegistered)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesRegistered = true;
        }

        // O cabecalho e sempre ASCII, entao da para le-lo antes de saber a
        // codificacao do corpo. 2 KB cobre folgado qualquer cabecalho OFX.
        int limit = Math.Min(content.Length, 2048);
        string header = Encoding.ASCII.GetString(content, 0, limit);

        // OFX 2.x: <?xml version="1.0" encoding="UTF-8"?>
        string? xmlEncoding = ReadQuotedValue(header, "encoding=");
        if (xmlEncoding is not null && TryGetEncoding(xmlEncoding, out Encoding? fromXml))
        {
            return fromXml;
        }

        // OFX 1.x: linhas CHARSET:1252 e ENCODING:USASCII
        string? charset = ReadHeaderValue(header, "CHARSET");
        if (charset is not null)
        {
            if (charset.Equals("UNICODE", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8;
            }

            if (int.TryParse(charset, NumberStyles.Integer, CultureInfo.InvariantCulture, out int codePage)
                && TryGetCodePage(codePage, out Encoding? fromCodePage))
            {
                return fromCodePage;
            }
        }

        string? declared = ReadHeaderValue(header, "ENCODING");
        if (declared is not null && declared.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        // USASCII sem CHARSET valido: 1252 e o palpite util, porque e o que
        // o banco quis dizer quando mandou acento num arquivo "ASCII".
        return TryGetCodePage(1252, out Encoding? fallback) ? fallback : Encoding.UTF8;
    }

    private static bool TryGetEncoding(string name, out Encoding encoding)
    {
        try
        {
            encoding = Encoding.GetEncoding(name);
            return true;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }

    private static bool TryGetCodePage(int codePage, out Encoding encoding)
    {
        try
        {
            encoding = Encoding.GetEncoding(codePage);
            return true;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
        catch (NotSupportedException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }

    private static string? ReadHeaderValue(string header, string key)
    {
        foreach (string line in header.Split('\n'))
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            int colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (trimmed[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed[(colon + 1)..].Trim().ToString();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? ReadQuotedValue(string text, string prefix)
    {
        int i = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        i += prefix.Length;
        if (i >= text.Length)
        {
            return null;
        }

        char quote = text[i];
        if (quote != '"' && quote != '\'')
        {
            return null;
        }

        int end = text.IndexOf(quote, i + 1);
        return end < 0 ? null : text[(i + 1)..end];
    }
}
