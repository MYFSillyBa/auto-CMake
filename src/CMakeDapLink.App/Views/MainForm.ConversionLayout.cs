namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private void LayoutConversionPage()
    {
        if (_conversionLayingOut || _conversionCards.Count == 0) return;
        _conversionLayingOut = true; _conversionPage.Canvas.SuspendLayout();
        try
        {
            _conversionOptions.Visible = _conversionInspection != null;
            _conversionActions.Visible = _conversionRoot != null;
            _conversionOutput.Visible = _conversionRoot != null;
            _conversionResult.Visible = _conversionResultShown;
            var (width, left) = PageBounds(_conversionPage); var inset = Px(20); var inner = width - inset * 2; var gap = Px(14);
            foreach (var card in _conversionCards) card.Width = width;
            var heroLabels = _conversionHero.Controls.OfType<Label>().ToArray();
            Fit(heroLabels[0], Px(2), Px(3), width - Px(4)); Fit(heroLabels[1], Px(2), heroLabels[0].Bottom + Px(5), width - Px(4)); _conversionHero.Height = heroLabels[1].Bottom + Px(5);
            var selectionTitle = _conversionSelection.Controls.OfType<Label>().First();
            var bottom = HeaderRow(selectionTitle, _conversionChoose, width, inset, Px(17));
            Fit(_conversionRootLabel, inset, bottom + Px(10), inner); _conversionSelection.Height = _conversionRootLabel.Bottom + Px(20);
            Fit(_conversionOptions.Controls.OfType<Label>().First(), inset, Px(18), inner); var y = Px(54);
            foreach (var pair in _conversionFields.Where(x => x.Field.Visible))
            {
                if (width >= Px(480)) { Fit(pair.Label, inset, y + Px(8), Px(126)); pair.Field.SetBounds(inset + Px(140), y, inner - Px(140), Px(36)); }
                else { Fit(pair.Label, inset, y, inner); pair.Field.SetBounds(inset, pair.Label.Bottom + Px(6), inner, Px(36)); }
                y = Math.Max(pair.Label.Bottom, pair.Field.Bottom) + Px(12);
            }
            if (_conversionRead.Visible) y = ButtonRow(width, inset, y, _conversionRead) + Px(14);
            Fit(_conversionSummary, inset, y + Px(2), inner); _conversionOptions.Height = _conversionSummary.Bottom + Px(20);
            Fit(_conversionActions.Controls.OfType<Label>().First(), inset, Px(18), inner);
            Fit(_conversionHint, inset, Px(54), inner); _conversionActions.Height = ButtonRow(width, inset, _conversionHint.Bottom + Px(14), _conversionApply, _conversionRefresh, _conversionHelp, _conversionRestore) + Px(20);
            Fit(_conversionOutput.Controls.OfType<Label>().First(), inset, Px(18), inner);
            _conversionSteps.Location = new(inset, Px(54)); _conversionSteps.Arrange(inner, Px(96) / 96f);
            Fit(_conversionStage, inset, _conversionSteps.Bottom + Px(12), inner);
            Fit(_conversionElapsed, inset, _conversionStage.Bottom + Px(5), inner);
            _conversionProgress.SetBounds(inset, _conversionElapsed.Bottom + Px(12), inner, Px(4));
            _conversionProblemsButton.Visible = _conversionProblems.Count > 0;
            y = ButtonRow(width, inset, _conversionProgress.Bottom + Px(14), new[] { _conversionLogToggle, _conversionProblemsButton }.Where(x => x.Visible).ToArray()) + Px(12);
            _conversionLog.Visible = _conversionLogExpanded;
            if (_conversionLogExpanded) { _conversionLog.SetBounds(inset, y, inner, Px(220)); y = _conversionLog.Bottom + Px(14); }
            _conversionOutput.Height = y + Px(7);
            Fit(_conversionResultTitle, inset, Px(18), inner); Fit(_conversionResultBody, inset, _conversionResultTitle.Bottom + Px(12), inner);
            var actions = new[] { _conversionOpen, _conversionNext }.Where(x => x.Visible).ToArray();
            _conversionResult.Height = ButtonRow(width, inset, _conversionResultBody.Bottom + Px(16), actions) + Px(20);
            if (_conversionNotice.Visible) _conversionNotice.Arrange(width, Px(96) / 96f);
            y = Px(22);
            foreach (var card in _conversionCards.Where(x => x.Visible)) { card.Location = new(left, y); y = card.Bottom + gap; }
            _conversionPage.SetContentHeight(y + Px(8));
        }
        finally { _conversionPage.Canvas.ResumeLayout(); _conversionLayingOut = false; }
    }
}
