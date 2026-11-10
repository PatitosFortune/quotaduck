using QuotaDuck.App.Presentation;
using QuotaDuck.App.Views;
using QuotaDuck.Art;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>The A+ 2 flyout: art-width maths, status chip, hero percentage, shared footer and the icon-button content. Pure logic; no window needed.</summary>
public class APlusLayoutTests
{
    private static readonly DateTimeOffset T = Snap.T0;

    private static CompactPresentation Compact(AppSnapshot s)
    {
        ShellPresentation p = Presenter.Present(s, RefreshFeedback.None, Snap.Utc);
        return new CompactPresentation(CompactCard.From(p.Claude), CompactCard.From(p.Codex), null, null, "Copy both", p.Claude.NoteBelongsInFooter ? p.Claude.Note : p.Claude.Footer);
    }

    private static ClaudeState Fresh() => new(Snap.Rec(42, T.AddHours(3), T.AddMinutes(-3)), Snap.Rec(12, T.AddDays(4), T.AddMinutes(-3)));

    // ---------------- art width and content width (pure) ----------------

    [Theory]
    [InlineData(1.0, 288.0)]
    [InlineData(1.25, 230.4)]
    [InlineData(1.5, 288.0)]
    [InlineData(2.0, 288.0)]
    public void ArtWidth_IsAWholeNumberScaleOfThePond_AndNeverWiderThanTheTarget(double dpi, double expectedDip)
    {
        Assert.Equal(expectedDip, CompactLayoutPolicy.ArtWidthDip(dpi), 6);
        Assert.True(CompactLayoutPolicy.ArtWidthDip(dpi) <= CompactLayoutPolicy.ArtTargetDip + 1e-9);
        double devicePx = CompactLayoutPolicy.ArtWidthDip(dpi) * dpi;
        Assert.Equal(Math.Round(devicePx), devicePx, 6);                                       // every art pixel is a whole block of device pixels
    }

    [Fact]
    public void PondAndGauge_ShareOneLogicalWidth_SoTheyAreExactlyAsWideAsEachOtherAtAnyDpi()
    {
        Assert.Equal(PondGeometry.Width, GaugeScene.GaugeWidth);
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
        {
            int kPond = PixelScale.Choose(PondGeometry.Width, CompactLayoutPolicy.ArtTargetDip, dpi);
            int kGauge = PixelScale.Choose(GaugeScene.GaugeWidth, CompactLayoutPolicy.ArtTargetDip, dpi);
            Assert.Equal(kPond, kGauge);
        }
    }

    [Theory]
    [InlineData(1.0, 656.0, 334.0)]
    [InlineData(2.0, 656.0, 334.0)]
    public void ContentWidth_IsTheCardsPlusTheirChrome(double dpi, double side, double stacked)
    {
        Assert.Equal(side, CompactLayoutPolicy.ContentWidthDip(CompactMode.SideBySide, dpi), 6);
        Assert.Equal(stacked, CompactLayoutPolicy.ContentWidthDip(CompactMode.Stacked, dpi), 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void ContentWidth_FitsWholeDevicePixels_AndHighContrastBordersWidenItByExactlyTheirThickness(double dpi)
    {
        foreach (CompactMode m in Enum.GetValues<CompactMode>())
        {
            double w = CompactLayoutPolicy.ContentWidthDip(m, dpi);
            Assert.Equal(Math.Round(w * dpi), w * dpi, 4);
            double hc = CompactLayoutPolicy.ContentWidthDip(m, dpi, cardBorderDip: 2);
            Assert.True(hc > w);
        }
    }

    // ---------------- status chip ----------------

    [Fact]
    public void Chip_Fresh_ForBothProviders_WithGlyphAndWord()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("Fresh", c.Claude.StatusWord);
        Assert.Equal("● Fresh", c.Claude.Chip);
        Assert.Equal("Fresh", c.Codex.StatusWord);
    }

    [Fact]
    public void Chip_Old_WhenClaudeHasNotReportedForHours_ButStillHasValues()
    {
        ClaudeState old = new(Snap.Rec(30, T.AddHours(1), T.AddHours(-4)), Snap.Rec(20, T.AddDays(3), T.AddHours(-4)));
        CompactPresentation c = Compact(Snap.Make(T, old, Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("Old", c.Claude.StatusWord);
        Assert.Equal("◐ Old", c.Claude.Chip);
        Assert.All(c.Claude.Rows, r => Assert.Equal("OLD", r.Badge));
    }

    [Fact]
    public void Chip_Unavailable_WhenCodexIsNotInstalled_AndThereAreNoRows()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(T, ProviderErrorKind.NotFound)));
        Assert.Equal("Unavailable", c.Codex.StatusWord);
        Assert.Equal("! Unavailable", c.Codex.Chip);
        Assert.Contains("not installed", c.Codex.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chip_Waiting_WhenNothingHasBeenReportedOrRead()
    {
        CompactPresentation c = Compact(Snap.Make(T));
        Assert.Equal("Waiting", c.Claude.StatusWord);
        Assert.Equal("Waiting", c.Codex.StatusWord);
    }

    [Fact]
    public void Chip_Off_WhenAProviderIsTurnedOff()
    {
        CompactPresentation c = Compact(Snap.Make(T, claudeEnabled: false));
        Assert.Equal("Off", c.Claude.StatusWord);
    }

    // ---------------- hero percentage, badges, never 0% ----------------

    [Fact]
    public void Hero_IsTheRemainingPercentage_WithASmallUnit()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal(("58%", "left"), (c.Claude.Rows[0].Hero, c.Claude.Rows[0].HeroUnit));
        Assert.Equal(("88%", "left"), (c.Claude.Rows[1].Hero, c.Claude.Rows[1].HeroUnit));
        Assert.Equal("", c.Claude.Rows[0].Badge);
    }

    [Fact]
    public void AnExpiredWindow_ShowsADash_NoValue_AndAResetBadge_NeverZeroPercent()
    {
        ClaudeState expired = new(Snap.Rec(60, T.AddMinutes(-20), T.AddHours(-4)), Snap.Rec(20, T.AddDays(4), T.AddHours(-4)));
        CompactPresentation c = Compact(Snap.Make(T, expired, Snap.Codex(T.AddMinutes(-1))));
        CompactRowModel five = c.Claude.Rows.Single(r => r.Label == "5-hour");
        Assert.Equal("RESET", five.Badge);
        Assert.Equal("–", five.Hero);
        Assert.Equal("no value", five.HeroUnit);
        Assert.DoesNotContain("0%", five.Hero);
        Assert.Contains("Reset 20 min ago", five.ResetLine);
        Assert.Equal("OLD", c.Claude.Rows.Single(r => r.Label == "Weekly").Badge);
    }

    [Fact]
    public void NoData_ShowsADash_AndSaysNoData_NeverZeroPercent()
    {
        CompactPresentation c = Compact(Snap.Make(T));
        Assert.All(c.Claude.Rows, r =>
        {
            Assert.Equal("–", r.Hero);
            Assert.Equal("no data", r.HeroUnit);
        });
    }

    [Fact]
    public void TheRowAccessibleName_IsUnchanged_ByTheRedesign()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("Claude 5-hour: 42% used, 58% left, Fresh, Resets in 3 h (13:00)", c.Claude.Rows[0].AccessibleName);
    }

    // ---------------- status line and age ----------------

    [Fact]
    public void AgeSuffix_IsShortened_AndNonBreaking_SoItWrapsAsOneUnit()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("changed 3 min ago", c.Claude.AgeText);
        Assert.Equal("read 1 min ago", c.Codex.AgeText);
        Assert.Equal(" · changed 3 min ago", c.Claude.AgeSuffix);
        Assert.DoesNotContain(" ", c.Claude.AgeSuffix[1..]);
    }

    [Fact]
    public void AgeSuffix_IsEmpty_WhenThereIsNoAge()
    {
        Assert.Equal(string.Empty, Compact(Snap.Make(T)).Claude.AgeSuffix);
    }

    // ---------------- shared footer ----------------

    [Fact]
    public void Footer_IsTheStandingTerminalOnlyNote_WhenClaudeReports()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("Claude reports only from terminal Claude Code sessions.", c.FooterNote);
        Assert.Null(c.Claude.Note);
        Assert.Null(c.Codex.Note);
    }

    [Fact]
    public void Footer_IsTheUsageElsewhereNote_WhenClaudeIsOld_AndItIsNotRepeatedInsideTheCard()
    {
        ClaudeState old = new(Snap.Rec(30, T.AddHours(1), T.AddHours(-4)), Snap.Rec(20, T.AddDays(3), T.AddHours(-4)));
        CompactPresentation c = Compact(Snap.Make(T, old, Snap.Codex(T.AddMinutes(-1))));
        Assert.StartsWith("Usage elsewhere (VS Code panel, Claude Desktop, claude.ai)", c.FooterNote);
        Assert.Null(c.Claude.Note);
    }

    [Fact]
    public void ACodexNote_StaysInsideItsOwnCard()
    {
        CompactPresentation c = Compact(Snap.Make(T, Fresh(), Snap.Codex(T.AddMinutes(-15)), codexIssue: ProviderIssue.TemporaryProviderFailure, attempt: new ProviderAttempt(T.AddMinutes(-1), ProviderErrorKind.Timeout)));
        Assert.Equal("Showing the last successful read.", c.Codex.Note);
        Assert.Equal("Problem", c.Codex.StatusWord);
    }

    // ---------------- icon buttons: content stays the status string; icons and labels derive from it ----------------

    [Theory]
    [InlineData("Refresh", "")]
    [InlineData("Refresh in 41 s", "41 s")]
    [InlineData("Refresh in 1 s", "1 s")]
    [InlineData("Reading…", "Reading…")]
    public void RefreshLabel_IsEmptyAtRest_AndKeepsTheCooldownSecondsVisible(string content, string label)
    {
        object result = new ButtonLabelConverter().Convert(content, typeof(string), "refresh", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(label, result);
    }

    [Theory]
    [InlineData("Copy")]
    [InlineData("Copied")]
    [InlineData("Not copied")]
    public void ProviderCopy_IsIconOnly_InEveryState(string content)
    {
        Assert.Equal(string.Empty, new ButtonLabelConverter().Convert(content, typeof(string), "copy", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Copy both")]
    [InlineData("Copied")]
    [InlineData("Not copied")]
    public void CopyBoth_KeepsItsVisibleLabel_InEveryState(string content)
    {
        Assert.Equal(content, new ButtonLabelConverter().Convert(content, typeof(string), "both", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheIcon_SwapsToACheckOrAWarning_AfterACopy_AndBackAtRest()
    {
        var conv = new ButtonIconConverter();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Same(ButtonIcons.Copy, conv.Convert("Copy", typeof(object), "copy", inv));
        Assert.Same(ButtonIcons.CopyBoth, conv.Convert("Copy both", typeof(object), "both", inv));
        Assert.Same(ButtonIcons.Refresh, conv.Convert("Refresh", typeof(object), "refresh", inv));
        Assert.Same(ButtonIcons.Check, conv.Convert("Copied", typeof(object), "copy", inv));
        Assert.Same(ButtonIcons.Check, conv.Convert("Copied", typeof(object), "both", inv));
        Assert.Same(ButtonIcons.Warn, conv.Convert("Not copied", typeof(object), "copy", inv));
        Assert.True(ButtonIcons.Copy.IsFrozen && ButtonIcons.Refresh.IsFrozen && ButtonIcons.CopyBoth.IsFrozen);
    }

    // ---------------- the XAML contract for the icon buttons ----------------

    [Fact]
    public void EveryButtonInTheXaml_HasAnExplicitAccessibleName_AndStaysKeyboardReachable()
    {
        string path = System.IO.Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App", "Views", "CompactFlyoutContent.xaml");
        var doc = System.Xml.Linq.XDocument.Load(path);
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        List<System.Xml.Linq.XElement> buttons = doc.Descendants(ui + "Button").ToList();
        Assert.Equal(5, buttons.Count);
        Assert.All(buttons, b =>
        {
            Assert.Contains(b.Attributes(), a => a.Name.LocalName == "AutomationProperties.Name" && a.Value.Length > 0);
            Assert.DoesNotContain(b.Attributes(), a => a.Name.LocalName is "Focusable" or "IsTabStop" && a.Value == "False");
        });
        Assert.All(buttons.Where(b => b.Attribute("ContentTemplate") is not null), b =>
            Assert.Contains(b.Attributes(), a => a.Name.LocalName == "ToolTip" && a.Value.Length > 0));          // icon-only buttons always carry a tooltip
        string xaml = System.IO.File.ReadAllText(path);
        Assert.DoesNotContain("ControlTemplate", xaml);                                                         // the system button template (focus rectangle, High Contrast) is kept
    }
}
