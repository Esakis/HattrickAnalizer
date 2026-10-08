using HattrickAnalizer.Services;
using HattrickAnalizer.Models;
using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace Backend.Tests;

public class TrainingServiceTests
{
    [Fact]
    public void Training_type_codes_2_through_12_map_to_supported_names_skills_and_positions()
    {
        var expected = new (int Code, string Name, string Skill, int SkillValue, double WingerFactor)[]
        {
            (2, "SetPieces", "SetPieces", 17, 1), (3, "Defending", "Defending", 13, -2),
            (4, "Scoring", "Scoring", 11, -2), (5, "Crossing", "Winger", 7, 1),
            (6, "Shooting", "Scoring", 11, -1), (7, "ShortPasses", "Passing", 5, 1),
            (8, "Playmaking", "Playmaking", 3, 0.5), (9, "Goalkeeping", "Keeper", 19, 0),
            (10, "ThroughPasses", "Passing", 5, 1), (11, "DefensivePositions", "Defending", 13, -1),
            (12, "WingAttacks", "Winger", 7, 1)
        };
        var typeName = typeof(TrainingService).GetMethod("TrainingTypeName", BindingFlags.NonPublic | BindingFlags.Static)!;
        var skillName = typeof(TrainingService).GetMethod("TrainedSkillName", BindingFlags.NonPublic | BindingFlags.Static)!;
        var skillValue = typeof(TrainingService).GetMethod("TrainedSkillValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        var skillAvailable = typeof(TrainingService).GetMethod("IsTrainedSkillAvailable", BindingFlags.NonPublic | BindingFlags.Static)!;
        var supported = typeof(TrainingService).GetMethod("IsSupportedTrainingTypeCode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var player = new Player { Skills = new PlayerSkills
        {
            Keeper = 19, KeeperAvailable = true, Defending = 13, DefendingAvailable = true,
            Playmaking = 3, PlaymakingAvailable = true, Winger = 7, WingerAvailable = true,
            Passing = 5, PassingAvailable = true, Scoring = 11, ScoringAvailable = true,
            SetPieces = 17, SetPiecesAvailable = true
        }};

        foreach (var (code, name, skill, value, wingerFactor) in expected)
        {
            Assert.Equal(name, typeName.Invoke(null, new object[] { code }));
            Assert.Equal(skill, skillName.Invoke(null, new object[] { code }));
            Assert.Equal(value, skillValue.Invoke(null, new object[] { player, code }));
            Assert.True((bool)skillAvailable.Invoke(null, new object[] { player, code })!);
            Assert.True((bool)supported.Invoke(null, new object[] { code })!);
            Assert.Equal(wingerFactor, TrainingService.TrainingFactor(code, "RW"));
        }
        Assert.StartsWith("Unknown(13)", typeName.Invoke(null, new object[] { 13 })?.ToString());
        Assert.Equal("", skillName.Invoke(null, new object[] { 13 }));
        Assert.Equal(0, skillValue.Invoke(null, new object[] { player, 13 }));
        Assert.False((bool)skillAvailable.Invoke(null, new object[] { player, 13 })!);
        Assert.False((bool)supported.Invoke(null, new object[] { 13 })!);
    }

    [Fact]
    public void Full_minutes_are_capped_first_then_half_effect_fills_the_remaining_90()
    {
        var mixed = TrainingService.ComputeBest90(50, 90, 0);
        var halfOnly = TrainingService.ComputeBest90(0, 90, 0);

        Assert.Equal(50, mixed.FullMinutes);
        Assert.Equal(40, mixed.HalfMinutes);
        Assert.Equal(70, mixed.EffectiveMinutes);
        Assert.Equal(45, halfOnly.EffectiveMinutes);
    }

    [Fact]
    public void Set_pieces_bonus_is_applied_only_to_bonus_role_segments()
    {
        var result = TrainingService.ComputeSetPiecesBest90(fullMinutes: 140, halfMinutes: 0,
            bonusFullMinutes: 50, bonusHalfMinutes: 0, smallEffectMinutes: 0);

        Assert.Equal(50, result.BonusFullMinutes);
        Assert.Equal(40, result.FullMinutes - result.BonusFullMinutes);
        Assert.Equal(90, result.EffectiveMinutes);
        Assert.Equal(12.5, result.SpecialBonusMinutes);
        Assert.Equal(102.5, result.EffectiveMinutes + result.SpecialBonusMinutes);
    }

    [Fact]
    public void Stamina_fraction_distinguishes_healthy_injured_and_partial_coverage()
    {
        Assert.Equal(0.5, TrainingService.StaminaTrainingFraction(false, 0, true));
        Assert.Equal(0, TrainingService.StaminaTrainingFraction(true, 0, true));
        Assert.Equal(0.875, TrainingService.StaminaTrainingFraction(false, 45, true)!.Value, 12);
        Assert.Null(TrainingService.StaminaTrainingFraction(false, 45, false));
    }

    [Fact]
    public void Assistant_speed_factor_uses_the_documented_bonus_and_unknown_is_null()
    {
        Assert.Equal(1.35, TrainingService.AssistantSpeedMultiplier(10)!.Value, 12);
        Assert.Null(TrainingService.AssistantSpeedMultiplier(null));
        Assert.Null(TrainingService.AssistantSpeedMultiplier(11));
    }

    [Fact]
    public void Training_window_uses_configured_timezone_and_week_boundary()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Training:UpdateDay"] = "Thursday",
            ["Training:UpdateHour"] = "22",
            ["Training:TimeZoneId"] = "Europe/Warsaw"
        }).Build();

        var before = TrainingService.GetTrainingWindow(new DateTimeOffset(2026, 10, 8, 19, 59, 0, TimeSpan.Zero), config);
        var atBoundary = TrainingService.GetTrainingWindow(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero), config);

        Assert.True(before.Configured);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero), before.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero), atBoundary.Start);
    }
}
