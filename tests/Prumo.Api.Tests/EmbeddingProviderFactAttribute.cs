namespace Prumo.Api.Tests;

/// <summary>
/// <c>[Fact]</c> condicional (MET-528, ADR-006): roda só quando um provedor de embeddings REAL está
/// configurado no ambiente — as MESMAS três variáveis que <c>src/Prumo.Seed</c>,
/// <c>src/Prumo.Eval</c> e <c>src/Prumo.SeedEmbeddings</c> já exigem para
/// <c>Embeddings:Provider=openai-compatible</c> (nenhuma variável nova para o operador aprender).
/// Sem elas configuradas — o caso da maioria das execuções, inclusive CI sem segredo nenhum — o
/// teste aparece como <c>Skipped</c>, nunca falha nem passa vacuamente, e a mensagem de skip diz
/// exatamente como rodá-lo de verdade.
///
/// <para>
/// <b>Por que isto, e não <c>Category=Integration</c>:</b> a categoria já usada no projeto
/// (<c>[Trait("Category", "Integration")]</c>) sobe Postgres+pgvector via Testcontainers — uma
/// dimensão ORTOGONAL a "existe um provedor de embeddings real no ar". Um teste marcado
/// <c>Category=Integration</c> exigiria Docker (a régua deste harness: "Docker indisponível ⇒
/// reporte, não pule em silêncio") para uma suíte que, na verdade, não abre banco nenhum — e ficaria
/// de fora do gate <c>full</c> (que filtra <c>Category!=Integration</c>), quando o próprio ponto
/// desta suíte é rodar sempre que possível, inclusive sem Docker, contra só a rede. Um
/// <c>[Fact]</c> comum falharia toda vez que ninguém tiver um provedor no ar (a maioria das
/// execuções); um <c>Skip</c> literal nunca provaria nada. Este atributo — <c>Skip</c> calculado no
/// CONSTRUTOR, a partir de variável de ambiente lida na DESCOBERTA de cada execução de
/// <c>dotnet test</c> (não em tempo de compilação) — é o meio-termo idiomático em xUnit v2: entra no
/// gate <c>full</c> normalmente (sem Docker) e vira execução real assim que alguém exportar as três
/// variáveis, sem precisar de outro comando/filtro.
/// </para>
/// </summary>
public sealed class EmbeddingProviderFactAttribute : FactAttribute
{
    public const string ProviderEnvironmentVariable = "Embeddings__Provider";
    public const string BaseUrlEnvironmentVariable = "Embeddings__BaseUrl";
    public const string ModelEnvironmentVariable = "Embeddings__Model";
    public const string RequiredProviderName = "openai-compatible";

    public EmbeddingProviderFactAttribute()
    {
        if (!IsConfigured())
        {
            Skip =
                "Requer um provedor de embeddings real no ar (nenhuma chamada de rede acontece sem isto " +
                $"configurado explicitamente). Exporte {ProviderEnvironmentVariable}={RequiredProviderName}, " +
                $"{BaseUrlEnvironmentVariable} (ex.: http://localhost:1234/v1, um LM Studio local servindo " +
                $"o modelo do artefato) e {ModelEnvironmentVariable} (ex.: text-embedding-qwen3-embedding-0.6b) " +
                "no shell ANTES de 'dotnet test' e rode de novo, a partir da raiz do repo (ver " +
                "db/seed/README.md, seção 'Reprodutibilidade do artefato — verificação executável').";
        }
    }

    /// <summary>
    /// Mesma checagem usada pelo construtor, exposta para quem quiser decidir em runtime (fora da
    /// descoberta de teste) se o provedor está configurado — evita duplicar a lógica em dois lugares.
    /// </summary>
    public static bool IsConfigured()
    {
        var provider = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable);
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
        var model = Environment.GetEnvironmentVariable(ModelEnvironmentVariable);

        return string.Equals(provider, RequiredProviderName, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(baseUrl)
            && !string.IsNullOrWhiteSpace(model);
    }
}