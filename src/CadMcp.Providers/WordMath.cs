using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Math;
namespace CadMcp.Providers;

/// <summary>A bounded parser that emits editable OMML; unsupported commands fail explicitly.</summary>
public static class WordMath
{
    private static readonly XNamespace M="http://schemas.openxmlformats.org/officeDocument/2006/math";
    public static OfficeMath Parse(string latex)
    {
        if(latex.Length>10000)throw new FormatException("Формула слишком длинная для экспорта Word");
        var parser=new Parser(latex);var root=new XElement(M+"oMath",parser.Sequence());
        if(!parser.End)throw new FormatException("Незавершённая формула Word");
        return new OfficeMath(root.ToString(SaveOptions.DisableFormatting));
    }
    private static XElement E(string name,params object[] children)=>new(M+name,children);
    private static XElement Run(string text)=>E("r",new XElement(M+"t",new XAttribute(XNamespace.Xml+"space","preserve"),text));
    private sealed class Parser(string text)
    {
        private int at;private int depth;public bool End=>at==text.Length;
        public List<XElement> Sequence(char stop='\0')
        {
            if(++depth>64)throw new FormatException("Слишком глубокая формула");var nodes=new List<XElement>();
            while(at<text.Length&&text[at]!=stop)
            {
                if(char.IsWhiteSpace(text[at])){at++;continue;}
                var atom=Atom();XElement? sub=null,sup=null;
                while(at<text.Length&&(text[at]=='_'||text[at]=='^')){char op=text[at++];var value=Group();if(op=='_'){if(sub is not null)throw new FormatException("Повторный индекс");sub=E("sub",value);}else{if(sup is not null)throw new FormatException("Повторная степень");sup=E("sup",value);}}
                if(sub is not null&&sup is not null)atom=E("sSubSup",E("e",atom),sub,sup);
                else if(sub is not null)atom=E("sSub",E("e",atom),sub);
                else if(sup is not null)atom=E("sSup",E("e",atom),sup);
                nodes.Add(atom);
            }
            depth--;return nodes;
        }
        private List<XElement> Group()
        {
            if(at>=text.Length)throw new FormatException("Не хватает аргумента формулы");
            if(text[at]!='{')return [Atom()];
            at++;var items=Sequence('}');if(at>=text.Length)throw new FormatException("Не закрыта группа формулы");at++;return items;
        }
        private string PlainGroup()
        {
            if(at>=text.Length||text[at++]!='{')throw new FormatException("Тексту формулы нужна группа");int start=at;
            while(at<text.Length&&text[at]!='}')at++;
            if(at>=text.Length)throw new FormatException("Не закрыта текстовая группа");string result=text[start..at];at++;return result;
        }
        private XElement Atom()
        {
            char c=text[at++];
            if(c=='{'){at--;return E("box",E("e",Group()));}
            if(c=='}')throw new FormatException("Лишняя скобка в формуле");
            if(c!='\\')return Run(c.ToString());
            int start=at;while(at<text.Length&&char.IsLetter(text[at]))at++;string command=text[start..at];
            if(command.Length==0){if(at==text.Length)throw new FormatException("Незавершённая команда");char escaped=text[at++];return Run(escaped is ',' or ';' or '!' or ' '?" ":escaped.ToString());}
            if(command=="begin")
            {
                string environment=PlainGroup();if(environment is not("matrix" or "pmatrix" or "bmatrix" or "vmatrix" or "cases" or "aligned"))throw new FormatException("Word: среда формулы "+environment+" не поддерживается");
                string closing="\\end{"+environment+"}";int end=text.IndexOf(closing,at,StringComparison.Ordinal);if(end<0)throw new FormatException("Не закрыта матрица");
                string content=text[at..end];at=end+closing.Length;var rows=content.Split("\\\\",StringSplitOptions.RemoveEmptyEntries);
                if(rows.Length>30)throw new FormatException("Матрица слишком велика");
                if(environment=="aligned")return E("eqArr",rows.Select(row=>E("e",new Parser(row.Replace("&","",StringComparison.Ordinal)).Sequence())).ToArray());
                var matrix=E("m",rows.Select(row=>E("mr",row.Split('&').Select(cell=>E("e",new Parser(cell).Sequence())).ToArray())).ToArray());
                if(environment=="matrix")return matrix;
                var delimiters=environment switch{"pmatrix"=>("(",")"),"bmatrix"=>("[","]"),"vmatrix"=>("|","|"),_=>("{","")};
                return E("d",E("dPr",new XElement(M+"begChr",new XAttribute(M+"val",delimiters.Item1)),new XElement(M+"endChr",new XAttribute(M+"val",delimiters.Item2))),E("e",matrix));
            }
            if(command is "frac" or "dfrac" or "tfrac")return E("f",E("num",Group()),E("den",Group()));
            if(command=="sqrt")
            {
                List<XElement> degree=[];
                if(at<text.Length&&text[at]=='['){at++;degree=Sequence(']');if(at==text.Length)throw new FormatException("Не закрыт порядок корня");at++;}
                return E("rad",E("radPr",new XElement(M+"degHide",new XAttribute(M+"val",degree.Count==0?"1":"0"))),E("deg",degree),E("e",Group()));
            }
            if(command is "text" or "mathrm" or "operatorname" or "textrm")return Run(PlainGroup());
            if(command is "mathbf" or "mathit")return E("box",E("e",Group()));
            if(command is "left" or "right" or "limits" or "nolimits")return Run("");
            if(command is "overline" or "bar")return E("bar",E("barPr",new XElement(M+"pos",new XAttribute(M+"val","top"))),E("e",Group()));
            if(command=="vec")return E("acc",E("accPr",new XElement(M+"chr",new XAttribute(M+"val","→"))),E("e",Group()));
            if(command is "sin" or "cos" or "tan" or "log" or "ln" or "exp" or "min" or "max")return Run(command);
            var symbols=new Dictionary<string,string>{{"alpha","α"},{"beta","β"},{"gamma","γ"},{"delta","δ"},{"epsilon","ε"},{"theta","θ"},{"lambda","λ"},{"mu","μ"},{"nu","ν"},{"pi","π"},{"rho","ρ"},{"sigma","σ"},{"tau","τ"},{"phi","φ"},{"psi","ψ"},{"omega","ω"},{"Gamma","Γ"},{"Delta","Δ"},{"Sigma","Σ"},{"Omega","Ω"},{"Phi","Φ"},{"infty","∞"},{"times","×"},{"cdot","·"},{"le","≤"},{"leq","≤"},{"ge","≥"},{"geq","≥"},{"ne","≠"},{"neq","≠"},{"approx","≈"},{"pm","±"},{"mp","∓"},{"sum","∑"},{"prod","∏"},{"int","∫"},{"partial","∂"},{"nabla","∇"},{"to","→"},{"rightarrow","→"},{"quad"," "},{"qquad","  "},{"ell","ℓ"},{"circ","°"},{"%","%"}};
            if(symbols.TryGetValue(command,out var symbol))return Run(symbol);
            throw new FormatException("Экспорт Word: команда формулы \\"+command+" пока не поддерживается. Формула не заменена картинкой или обычным текстом.");
        }
    }
}
