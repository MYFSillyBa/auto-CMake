namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private (int Width, int Left) PageBounds(ScrollPage page)
    {
        var margin = Px(page.ViewportWidth < Px(650) ? 14 : 22);
        var width = Math.Min(Px(1100), Math.Max(Px(180), page.ViewportWidth - margin * 2));
        return (width, Math.Max(margin, (page.ViewportWidth - width) / 2));
    }

    private int HeaderRow(Label title, Button button, int width, int inset, int top)
    {
        var inner = width - inset * 2;
        button.Size = new Size(Math.Min(inner, ButtonWidth(button, 96)), ButtonHeight(button, 32));
        if (inner >= button.Width + Px(156))
        {
            Fit(title, inset, top + Px(3), inner - button.Width - Px(14));
            button.Location = new Point(width - inset - button.Width, top);
        }
        else
        {
            Fit(title, inset, top, inner);
            button.Location = new Point(inset, title.Bottom + Px(8));
        }
        return Math.Max(title.Bottom, button.Bottom);
    }

    private int ButtonRow(int width, int inset, int top, params Button[] buttons)
    {
        var x = inset; var y = top; var bottom = top;
        foreach (var button in buttons)
        {
            button.Size = new Size(Math.Min(width - inset * 2, ButtonWidth(button, 64)), ButtonHeight(button, 32));
            if (x > inset && x + button.Width > width - inset) { x = inset; y = bottom + Px(8); }
            button.Location = new Point(x, y); x = button.Right + Px(8); bottom = Math.Max(bottom, button.Bottom);
        }
        return bottom;
    }

    private void LayoutPage()
    {
        if (_layingOut || _cards.Count == 0) return;
        _layingOut = true;
        _flow.Canvas.SuspendLayout();
        try
        {
            var (width, left) = PageBounds(_flow);
            var inset = Px(20);
            var inner = Math.Max(1, width - inset * 2);
            var gap = Px(14);
            var columns = width >= Px(650);
            foreach (var card in _cards) card.Width = width;
            Fit(_heroTitle, Px(2), Px(3), width - Px(4));
            Fit(_heroSubtitle, Px(2), _heroTitle.Bottom + Px(5), width - Px(4));
            _hero.Height = _heroSubtitle.Bottom + Px(5);

            var textLeft = Px(64);
            _browseProject.Size = new Size(Math.Min(width - textLeft - Px(18), ButtonWidth(_browseProject, 96)), ButtonHeight(_browseProject, 34));
            var inlineBrowse = width >= Px(460);
            var textWidth = width - textLeft - Px(18) - (inlineBrowse ? _browseProject.Width + Px(18) : 0);
            Fit(_dropTitle, textLeft, Px(18), textWidth);
            _folder.SetBounds(textLeft, _dropTitle.Bottom + Px(5), textWidth, FontLineHeight(_folder));
            _browseProject.Location = inlineBrowse
                ? new Point(width - Px(18) - _browseProject.Width, Px(24))
                : new Point(textLeft, _folder.Bottom + Px(9));
            _folderIcon.SetBounds(Px(20), Px(26), Px(28), Px(28));
            _drop.Height = Math.Max(_folder.Bottom, _browseProject.Bottom) + Px(17);

            var summaryWidth = columns ? (int)((width - gap) * .62) : width;
            _summary.Width = summaryWidth;
            var summaryInner = summaryWidth - inset * 2;
            var summaryTitle = _summary.Controls.OfType<Label>().First();
            var headerBottom = HeaderRow(summaryTitle, _rescan, summaryWidth, inset, Px(16));
            Fit(_result, inset, headerBottom + Px(10), summaryInner);
            var tileColumns = summaryInner >= Px(270) ? 2 : 1;
            var tileWidth = (summaryInner - (tileColumns - 1) * Px(9)) / tileColumns;
            var tiles = _toolTiles.Values.ToArray();
            var tileHeight = tiles.Max(tile => tile.Arrange(tileWidth, Px(11)));
            for (var i = 0; i < tiles.Length; i++)
                tiles[i].SetBounds(inset + i % tileColumns * (tileWidth + Px(9)),
                    _result.Bottom + Px(13) + i / tileColumns * (tileHeight + Px(9)), tileWidth, tileHeight);
            _summary.Height = tiles.Max(x => x.Bottom) + Px(18);

            _action.Width = columns ? width - gap - summaryWidth : width;
            var actionInner = _action.Width - inset * 2;
            var actionTitle = _action.Controls.OfType<Label>().First();
            Fit(actionTitle, inset, Px(18), actionInner);
            Fit(_actionNote, inset, actionTitle.Bottom + Px(13), actionInner);
            _configure.Size = new Size(columns ? actionInner : Math.Min(actionInner, ButtonWidth(_configure, 146)), ButtonHeight(_configure, 36));
            _configure.Location = new Point(inset, _actionNote.Bottom + Px(20));
            var repairBottom = ButtonRow(_action.Width, inset, _configure.Bottom + Px(9), _autoRepair, _mirrorSource);
            Fit(_readiness, inset, repairBottom + Px(14), actionInner);
            var helpBottom = ButtonRow(_action.Width, inset, _readiness.Bottom + Px(8), _repair, _environmentHelp);
            _action.Height = helpBottom + Px(18);
            if (columns) _action.Height = _summary.Height = Math.Max(_action.Height, _summary.Height);

            var detailsTitle = _details.Controls.OfType<Label>().First();
            var detailsHeader = HeaderRow(detailsTitle, _toggle, width, inset, Px(14));
            Fit(_detailsHint, inset, detailsHeader + Px(4), inner);
            var detailsBottom = LayoutDetailRows(width, _detailsHint.Bottom + Px(15));
            _details.Height = (_expanded ? detailsBottom : _detailsHint.Bottom) + Px(16);

            var outputTitle = _output.Controls.OfType<Label>().First();
            Fit(outputTitle, inset, Px(16), inner);
            Fit(_stage, inset, outputTitle.Bottom + Px(5), inner);
            _progress.SetBounds(inset, _stage.Bottom + Px(10), inner, Px(3));
            _log.SetBounds(inset, _progress.Bottom + Px(12), inner, Px(126));
            _output.Height = _log.Bottom + Px(18);

            var top = Px(18);
            _hero.Location = new Point(left, top); top = _hero.Bottom + Px(12);
            _drop.Location = new Point(left, top); top = _drop.Bottom + gap;
            _summary.Location = new Point(left, top);
            if (columns)
            {
                _action.Location = new Point(_summary.Right + gap, top);
                top = _summary.Bottom + gap;
            }
            else
            {
                _action.Location = new Point(left, _summary.Bottom + gap);
                top = _action.Bottom + gap;
            }
            _details.Location = new Point(left, top);
            _output.Location = new Point(left, _details.Bottom + gap);
            _flow.SetContentHeight(_output.Bottom + Px(20));
        }
        finally { _flow.Canvas.ResumeLayout(); _layingOut = false; }
    }

    private void LayoutSourcePage()
    {
        if (_sourceCards.Count == 0 || _layingOutSources) return;
        _layingOutSources = true;
        _sources.Canvas.SuspendLayout();
        try
        {
            var (width, left) = PageBounds(_sources);
            var inset = Px(20);
            var gap = Px(14);
            var columns = width >= Px(650);
            foreach (var card in _sourceCards) card.Width = width;
            var title = _sourceHero.Controls.OfType<Label>().First();
            var subtitle = _sourceHero.Controls.OfType<Label>().Skip(1).First();
            Fit(title, Px(2), Px(3), width - Px(4));
            Fit(subtitle, Px(2), title.Bottom + Px(5), width - Px(4));
            _sourceHero.Height = subtitle.Bottom + Px(5);

            var pickWidth = columns ? (int)((width - gap) * .43) : width;
            var pickInner = pickWidth - inset * 2;
            _sourcePick.Width = pickWidth;
            var pickTitle = _sourcePick.Controls.OfType<Label>().First();
            Fit(pickTitle, inset, Px(18), pickInner);
            _sourceProject.AutoEllipsis = true;
            _sourceProject.SetBounds(inset, pickTitle.Bottom + Px(10), pickInner, FontLineHeight(_sourceProject));
            _chooseSource.SetBounds(inset, _sourceProject.Bottom + Px(15), Math.Min(pickInner, ButtonWidth(_chooseSource, 170)), ButtonHeight(_chooseSource, 34));
            _sourceFolder.SetBounds(inset, _chooseSource.Bottom + Px(8), pickInner, FontLineHeight(_sourceFolder));
            var targetLabel = _sourcePick.Controls.OfType<Label>().Single(x => x.Text == "CMake 目标");
            Fit(targetLabel, inset, _sourceFolder.Bottom + Px(14), pickInner);
            _sourceTarget.SetBounds(inset, targetLabel.Bottom + Px(6), pickInner, ButtonHeight(_sourceTarget, 36));
            Fit(_sourceFolderHint, inset, _sourceTarget.Bottom + Px(17), pickInner);
            _sourcePick.Height = _sourceFolderHint.Bottom + Px(18);

            _sourceFiles.Width = columns ? width - gap - pickWidth : width;
            var fileInner = _sourceFiles.Width - inset * 2;
            var filesTitle = _sourceFiles.Controls.OfType<Label>().First();
            Fit(filesTitle, inset, Px(18), fileInner);
            Fit(_sourceCount, inset, filesTitle.Bottom + Px(10), fileInner);
            var selectBottom = ButtonRow(_sourceFiles.Width, inset, _sourceCount.Bottom + Px(8), _selectAllSources, _selectNoSources);
            var previewTop = selectBottom + Px(12);
            Fit(_sourceIncludes, inset, 0, fileInner);
            _sourceFiles.Height = Math.Max(previewTop + Px(210) + _sourceIncludes.Height + Px(30), columns ? _sourcePick.Height : 0);
            if (columns) _sourcePick.Height = _sourceFiles.Height;
            _sourcePreview.UiScale = _previewScale ?? DeviceDpi / 96f;
            _sourcePreview.SetBounds(inset, previewTop, fileInner, _sourceFiles.Height - previewTop - _sourceIncludes.Height - Px(30));
            _sourceIncludes.Top = _sourcePreview.Bottom + Px(10);

            var inner = width - inset * 2;
            var actionTitle = _sourceAction.Controls.OfType<Label>().First();
            var actionHeader = HeaderRow(actionTitle, _applySource, width, inset, Px(16));
            Fit(_sourceStatus, inset, actionHeader + Px(8), inner);
            _sourceProgress.SetBounds(inset, _sourceStatus.Bottom + Px(10), inner, Px(3));
            _sourceLog.SetBounds(inset, _sourceProgress.Bottom + Px(12), inner, Px(110));
            _sourceAction.Height = _sourceLog.Bottom + Px(18);

            _sourceHero.Location = new Point(left, Px(18));
            var top = _sourceHero.Bottom + Px(12);
            _sourcePick.Location = new Point(left, top);
            _sourceFiles.Location = columns ? new Point(_sourcePick.Right + gap, top) : new Point(left, _sourcePick.Bottom + gap);
            _sourceAction.Location = new Point(left, _sourceFiles.Bottom + gap);
            _sources.SetContentHeight(_sourceAction.Bottom + Px(20));
        }
        finally { _sources.Canvas.ResumeLayout(); _layingOutSources = false; }
    }

    private void LayoutImportPage()
    {
        if (_importCards.Count == 0 || _layingOutImports) return;
        _layingOutImports = true; _imports.Canvas.SuspendLayout();
        try
        {
            var (width, left) = PageBounds(_imports);
            var inset = Px(20); var gap = Px(14); var columns = width >= Px(650);
            foreach (var card in _importCards) card.Width = width;
            var title = _importHero.Controls.OfType<Label>().First();
            var subtitle = _importHero.Controls.OfType<Label>().Skip(1).First();
            Fit(title, Px(2), Px(3), width - Px(4));
            Fit(subtitle, Px(2), title.Bottom + Px(5), width - Px(4));
            _importHero.Height = subtitle.Bottom + Px(5);

            var pickWidth = columns ? (int)((width - gap) * .43) : width;
            _importDetails.Width = pickWidth;
            var pickInner = pickWidth - inset * 2;
            var pickTitle = _importDetails.Controls.OfType<Label>().First();
            Fit(pickTitle, inset, Px(18), pickInner);
            _importProject.SetBounds(inset, pickTitle.Bottom + Px(10), pickInner, FontLineHeight(_importProject));
            var nameLabel = _importDetails.Controls.OfType<Label>().Single(x => x.Text == "文件夹名称");
            Fit(nameLabel, inset, _importProject.Bottom + Px(18), pickInner);
            _importName.SetBounds(inset, nameLabel.Bottom + Px(6), pickInner, _importName.PreferredHeight);
            _importDestination.SetBounds(inset, _importName.Bottom + Px(10), pickInner, FontLineHeight(_importDestination));
            Fit(_importNote, inset, _importDestination.Bottom + Px(18), pickInner);
            _importDetails.Height = _importNote.Bottom + Px(20);

            _importFiles.Width = columns ? width - gap - pickWidth : width;
            var fileInner = _importFiles.Width - inset * 2;
            var filesTitle = _importFiles.Controls.OfType<Label>().First();
            Fit(filesTitle, inset, Px(18), fileInner);
            var buttonsBottom = ButtonRow(_importFiles.Width, inset, filesTitle.Bottom + Px(12), _chooseImport, _clearImport);
            Fit(_importCount, inset, buttonsBottom + Px(10), fileInner);
            var previewTop = _importCount.Bottom + Px(12);
            _importFiles.Height = Math.Max(previewTop + Px(205) + Px(18), columns ? _importDetails.Height : 0);
            if (columns) _importDetails.Height = _importFiles.Height;
            _importList.UiScale = _previewScale ?? DeviceDpi / 96f;
            _importList.SetBounds(inset, previewTop, fileInner, _importFiles.Height - previewTop - Px(18));

            var inner = width - inset * 2;
            var resultTitle = _importResult.Controls.OfType<Label>().First();
            Fit(resultTitle, inset, Px(18), inner);
            Fit(_importStatus, inset, resultTitle.Bottom + Px(9), inner);
            var resultButtons = ButtonRow(width, inset, _importStatus.Bottom + Px(13), _copyFiles, _openImported);
            _importProgress.SetBounds(inset, resultButtons + Px(14), inner, Px(3));
            _importLog.SetBounds(inset, _importProgress.Bottom + Px(12), inner, Px(90));
            _importResult.Height = _importLog.Bottom + Px(18);

            _importHero.Location = new Point(left, Px(18));
            var top = _importHero.Bottom + Px(12);
            _importDetails.Location = new Point(left, top);
            _importFiles.Location = columns ? new Point(_importDetails.Right + gap, top) : new Point(left, _importDetails.Bottom + gap);
            _importResult.Location = new Point(left, _importFiles.Bottom + gap);
            _imports.SetContentHeight(_importResult.Bottom + Px(20));
        }
        finally { _imports.Canvas.ResumeLayout(); _layingOutImports = false; }
    }
}
