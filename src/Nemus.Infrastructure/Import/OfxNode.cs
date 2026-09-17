using System.Text;

namespace Nemus.Infrastructure.Import;

/// <summary>
/// No da arvore de um documento OFX.
///
/// POR QUE NAO USAR UM PARSER DE XML. A especificacao OFX tem duas versoes
/// bem diferentes: a 2.x e XML de verdade, mas a 1.x e SGML - e e essa que
/// praticamente todo banco brasileiro emite. Em SGML a tag de valor NAO E
/// FECHADA:
///
///     &lt;STMTTRN&gt;
///     &lt;TRNTYPE&gt;DEBIT
///     &lt;DTPOSTED&gt;20240115120000[-03:BRT]
///     &lt;TRNAMT&gt;-45.90
///     &lt;FITID&gt;202401150001
///     &lt;/STMTTRN&gt;
///
/// XDocument engasga nisso na primeira linha. A alternativa comum e
/// converter SGML para XML com expressao regular antes de entregar ao
/// parser - e isso quebra em toda descricao que contenha "&lt;" ou "&amp;",
/// que aparece mais do que se imagina em nome de estabelecimento.
///
/// Um tokenizador tolerante resolve os dois formatos com o mesmo codigo, e
/// e o unico lugar do sistema que precisa entender o formato.
/// </summary>
public sealed class OfxNode
{
    private readonly List<OfxNode> _children = [];

    public OfxNode(string name) => Name = name;

    public string Name { get; }

    /// <summary>Conteudo textual, quando e folha. Nulo em no de agregacao.</summary>
    public string? Value { get; internal set; }

    public IReadOnlyList<OfxNode> Children => _children;

    internal void Add(OfxNode child) => _children.Add(child);

    /// <summary>Primeiro filho direto com este nome, ignorando caixa.</summary>
    public OfxNode? Child(string name) =>
        _children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valor de um filho direto, ou nulo.</summary>
    public string? ChildValue(string name) => Child(name)?.Value;

    /// <summary>
    /// Todos os descendentes com este nome, em qualquer profundidade. O OFX
    /// aninha STMTTRN dentro de BANKTRANLIST dentro de STMTRS dentro de
    /// STMTTRNRS dentro de BANKMSGSRSV1 - e a profundidade varia por banco.
    /// Procurar em profundidade evita depender do caminho exato.
    /// </summary>
    public IEnumerable<OfxNode> Descendants(string name)
    {
        foreach (OfxNode child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
            }

            foreach (OfxNode descendant in child.Descendants(name))
            {
                yield return descendant;
            }
        }
    }

    public override string ToString()
    {
        var text = new StringBuilder(Name);
        if (Value is not null)
        {
            text.Append('=').Append(Value);
        }
        else if (_children.Count > 0)
        {
            text.Append('[').Append(_children.Count).Append(']');
        }

        return text.ToString();
    }
}
