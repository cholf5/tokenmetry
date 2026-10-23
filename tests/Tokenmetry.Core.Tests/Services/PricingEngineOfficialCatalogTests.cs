namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// P-001 端到端回归：ModelCatalog 远程层经 OfficialPricingCatalog 投影后，PricingEngine 的解析结果。
/// 罐头数据复刻 2026-10-21 事故形态（scnet-token-plan/GLM-5.3-Flash 全零条目在真实数据上压过 20+ 有价条目）。
/// </summary>
public class PricingEngineOfficialCatalogTests
{
    private const string IncidentShapedJson = """
        {
          "scnet-token-plan": {
            "id": "scnet-token-plan",
            "models": {
              "GLM-5.3-Flash": {
                "id": "GLM-5.3-Flash",
                "cost": {
                  "input": 0,
                  "output": 0,
                  "cache_read": 0,
                  "cache_write": 0
                }
              }
            }
          },
          "privatemode-ai": {
            "id": "privatemode-ai",
            "models": {
              "glm-5.3-flash": {
                "id": "glm-5.3-flash",
                "cost": {
                  "input": 0.2311,
                  "output": 0.7511,
                  "cache_read": 0.0578
                }
              }
            }
          },
          "opencode": {
            "id": "opencode",
            "models": {
              "glm-5.3-flash-free": {
                "id": "glm-5.3-flash-free",
                "cost": {
                  "input": 0,
                  "output": 0,
                  "cache_read": 0
                }
              }
            }
          },
          "zai": {
            "id": "zai",
            "models": {
              "glm-5.3-flash": {
                "id": "glm-5.3-flash",
                "cost": {
                  "input": 0.15,
                  "output": 0.5,
                  "cache_read": 0.03,
                  "cache_write": 0
                }
              },
              "glm-5.3-fast": {
                "id": "glm-5.3-fast",
                "cost": {
                  "input": 2.1,
                  "output": 6.6,
                  "cache_read": 0.21
                }
              }
            }
          },
          "deepseek": {
            "id": "deepseek",
            "models": {
              "deepseek-flash": {
                "id": "deepseek-flash",
                "cost": {
                  "input": 0.14,
                  "output": 0.28,
                  "cache_read": 0.0028
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void Incident_glm_flash_is_priced_from_zai_not_the_all_zero_entry()
    {
        var engine = EngineWithRemote(IncidentShapedJson);

        var result = engine.PriceTokens("glm-5.3-flash", Tokens(1_000_000, 1_000_000, 1_000_000));

        result.CostUsd.Should().BeGreaterThan(0m);
        result.MatchedKey.Should().Be("zai/glm-5.3-flash");
        result.CostUsd.Should().BeApproximately(0.68m, 0.0001m);
    }

    [Fact]
    public void Real_official_fast_variant_keeps_its_own_price_exact_first()
    {
        var engine = EngineWithRemote(IncidentShapedJson);

        var result = engine.PriceTokens("glm-5.3-fast", Tokens(1_000_000, 1_000_000, 1_000_000));

        result.MatchedKey.Should().Be("zai/glm-5.3-fast");
        result.CostUsd.Should().BeApproximately(8.91m, 0.0001m);
    }

    [Fact]
    public void Relay_invented_free_suffix_falls_back_to_official_paid_price()
    {
        var engine = EngineWithRemote(IncidentShapedJson);

        var result = engine.PriceTokens("glm-5.3-flash-free", Tokens(1_000_000, 1_000_000, 1_000_000));

        result.MatchedKey.Should().Be("zai/glm-5.3-flash");
        result.CostUsd.Should().BeApproximately(0.68m, 0.0001m);
    }

    [Fact]
    public void Model_absent_from_every_layer_stays_missing()
    {
        var engine = EngineWithRemote(IncidentShapedJson);

        var result = engine.PriceTokens("ark-code-latest", Tokens(1_000_000, 1_000_000, 1_000_000));

        result.Source.Should().Be(PricingSourceKind.Missing);
        result.CostUsd.Should().BeNull();
    }

    [Fact]
    public void Offline_fallback_to_snapshot_is_unchanged()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog(null));

        var result = engine.PriceTokens("glm-5.3-flash", Tokens(1_000_000, 1_000_000, 1_000_000));

        result.MatchedKey.Should().Be("zai/glm-5.3-flash");
        result.CostUsd.Should().BeApproximately(0.68m, 0.0001m);
    }

    private static PricingEngine EngineWithRemote(string json)
    {
        var entries = ModelsDevPricingParser.Parse(json);
        entries.Should().NotBeEmpty();
        return new PricingEngine(catalog: new ModelCatalog(new FakeRemoteSource(entries)));
    }

    private static TokenUsageDetail Tokens(long input, long output, long cacheRead) =>
        TokenUsageDetail.FromRecord(new UsageRecord
        {
            Timestamp = DateTimeOffset.Now,
            Agent = "ZCode",
            ProjectPath = "/tmp",
            SessionId = "s",
            Model = "probe",
            CostUsd = null,
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = 0,
            CacheReadTokens = cacheRead,
            MessageId = "m",
        });

    private sealed class FakeRemoteSource(IReadOnlyList<ModelPriceEntry> entries) : IRemotePricingSource
    {
        public RemotePricingSnapshot? Current { get; } =
            new() { FetchedAt = DateTimeOffset.Now, Entries = entries };
    }
}
