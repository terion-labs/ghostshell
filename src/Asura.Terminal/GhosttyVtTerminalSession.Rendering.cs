using System.Text;
using Asura.Application;
using Asura.Terminal.GhosttyVt;

namespace Asura.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    private const int MaximumCapturedCells = 262_144;
    private const int MaximumPlainTextCharacters = 1_048_576;

    private unsafe TerminalRenderFrame BuildRenderFrameUnsafe()
    {
        if (_cachedRenderFrame is not null
            && _renderedContentRevision == _contentRevision)
        {
            return _cachedRenderFrame;
        }

        EnsureSuccess(
            GhosttyVtNative.RenderStateUpdate(_renderState, _terminal),
            "update terminal render state");

        ushort columns = 0;
        ushort rows = 0;
        GhosttyVtRenderStateDirty nativeDirty = GhosttyVtRenderStateDirty.Clean;
        EnsureSuccess(
            GhosttyVtNative.RenderStateGet(
                _renderState,
                GhosttyVtRenderStateData.Columns,
                &columns),
            "read render columns");
        EnsureSuccess(
            GhosttyVtNative.RenderStateGet(
                _renderState,
                GhosttyVtRenderStateData.Rows,
                &rows),
            "read render rows");
        EnsureSuccess(
            GhosttyVtNative.RenderStateGet(
                _renderState,
                GhosttyVtRenderStateData.Dirty,
                &nativeDirty),
            "read render damage");

        var colors = GhosttyVtRenderStateColors.CreateSized();
        EnsureSuccess(
            GhosttyVtNative.RenderStateColorsGet(_renderState, &colors),
            "read render colors");

        var rowIterator = _rowIterator.DangerousGetHandle();
        EnsureSuccess(
            GhosttyVtNative.RenderStateGet(
                _renderState,
                GhosttyVtRenderStateData.RowIterator,
                &rowIterator),
            "populate render-row iterator");

        var renderRows = new List<TerminalRenderRow>(rows);
        var dirtyRows = new List<int>();
        var rowIndex = 0;
        while (GhosttyVtNative.RenderRowIteratorNext(_rowIterator))
        {
            byte rowDirty = 0;
            ulong rawRow = 0;
            EnsureSuccess(
                GhosttyVtNative.RenderRowGet(
                    _rowIterator,
                    GhosttyVtRenderRowData.Dirty,
                    &rowDirty),
                "read render-row damage");
            EnsureSuccess(
                GhosttyVtNative.RenderRowGet(
                    _rowIterator,
                    GhosttyVtRenderRowData.Raw,
                    &rawRow),
                "read raw terminal row");
            if (rowDirty != 0)
            {
                dirtyRows.Add(rowIndex);
            }

            var cellsHandle = _rowCells.DangerousGetHandle();
            EnsureSuccess(
                GhosttyVtNative.RenderRowGet(
                    _rowIterator,
                    GhosttyVtRenderRowData.Cells,
                    &cellsHandle),
                "populate render-cell iterator");

            var cells = new List<TerminalRenderCell>(columns);
            var column = 0;
            while (GhosttyVtNative.RenderRowCellsNext(_rowCells))
            {
                cells.Add(ReadRenderCellUnsafe(rowIndex, column, &colors));
                column++;
            }

            while (cells.Count < columns)
            {
                cells.Add(CreateBlankCell());
            }

            if (cells.Count > columns)
            {
                cells.RemoveRange(columns, cells.Count - columns);
            }

            renderRows.Add(new TerminalRenderRow(
                rowIndex,
                cells,
                IsWrapped: ReadRowFlag(rawRow, GhosttyVtRowData.Wrap),
                IsWrapContinuation: ReadRowFlag(rawRow, GhosttyVtRowData.WrapContinuation),
                SemanticRole: ReadRowSemanticRole(rawRow),
                ContainsKittyVirtualPlaceholder: ReadRowFlag(
                    rawRow,
                    GhosttyVtRowData.KittyVirtualPlaceholder)));

            byte clean = 0;
            EnsureSuccess(
                GhosttyVtNative.RenderRowSet(
                    _rowIterator,
                    GhosttyVtRenderRowOption.Dirty,
                    &clean),
                "acknowledge render-row damage");
            rowIndex++;
        }

        while (renderRows.Count < rows)
        {
            var blank = new TerminalRenderCell[columns];
            for (var index = 0; index < blank.Length; index++)
            {
                blank[index] = CreateBlankCell();
            }

            renderRows.Add(new TerminalRenderRow(renderRows.Count, blank));
        }

        var damageKind = nativeDirty switch
        {
            GhosttyVtRenderStateDirty.Clean => TerminalRenderDamageKind.None,
            GhosttyVtRenderStateDirty.Partial when dirtyRows.Count > 0 =>
                TerminalRenderDamageKind.Partial,
            GhosttyVtRenderStateDirty.Partial => TerminalRenderDamageKind.Full,
            GhosttyVtRenderStateDirty.Full => TerminalRenderDamageKind.Full,
            _ => TerminalRenderDamageKind.Full,
        };
        var cursor = ReadRenderCursorUnsafe(&colors);
        var kittyGraphics = ReadKittyGraphicsUnsafe();

        var cleanState = GhosttyVtRenderStateDirty.Clean;
        EnsureSuccess(
            GhosttyVtNative.RenderStateSet(
                _renderState,
                GhosttyVtRenderStateOption.Dirty,
                &cleanState),
            "acknowledge render damage");

        _columns = columns;
        _rows = rows;
        _renderRevision++;
        _renderedContentRevision = _contentRevision;
        _cachedRenderFrame = new TerminalRenderFrame(
            _renderRevision,
            rows,
            columns,
            renderRows,
            cursor,
            new TerminalRenderDelta(damageKind, dirtyRows),
            kittyGraphics);
        return _cachedRenderFrame;
    }

    private unsafe TerminalRenderCell ReadRenderCellUnsafe(
        int row,
        int column,
        GhosttyVtRenderStateColors* colors)
    {
        ulong rawCell = 0;
        byte selected = 0;
        byte hasStyling = 0;
        EnsureSuccess(
            GhosttyVtNative.RenderRowCellsGet(
                _rowCells,
                GhosttyVtRenderCellData.Raw,
                &rawCell),
            "read raw terminal cell");
        EnsureSuccess(
            GhosttyVtNative.RenderRowCellsGet(
                _rowCells,
                GhosttyVtRenderCellData.Selected,
                &selected),
            "read terminal-cell selection");
        EnsureSuccess(
            GhosttyVtNative.RenderRowCellsGet(
                _rowCells,
                GhosttyVtRenderCellData.HasStyling,
                &hasStyling),
            "read terminal-cell style marker");

        var foreground = colors->Foreground;
        var background = colors->Background;
        var foregroundResult = GhosttyVtNative.RenderRowCellsGet(
            _rowCells,
            GhosttyVtRenderCellData.ForegroundColor,
            &foreground);
        if (foregroundResult is not (GhosttyVtResult.Success or GhosttyVtResult.InvalidValue))
        {
            EnsureSuccess(foregroundResult, "read terminal-cell foreground");
        }

        var backgroundResult = GhosttyVtNative.RenderRowCellsGet(
            _rowCells,
            GhosttyVtRenderCellData.BackgroundColor,
            &background);
        if (backgroundResult is not (GhosttyVtResult.Success or GhosttyVtResult.InvalidValue))
        {
            EnsureSuccess(backgroundResult, "read terminal-cell background");
        }

        GhosttyVtCellWide nativeWidth = GhosttyVtCellWide.Narrow;
        byte isProtected = 0;
        GhosttyVtCellSemanticContent semantic = GhosttyVtCellSemanticContent.Output;
        EnsureSuccess(
            GhosttyVtNative.CellGet(rawCell, GhosttyVtCellData.Wide, &nativeWidth),
            "read terminal-cell width");
        EnsureSuccess(
            GhosttyVtNative.CellGet(rawCell, GhosttyVtCellData.Protected, &isProtected),
            "read terminal-cell protection");
        EnsureSuccess(
            GhosttyVtNative.CellGet(rawCell, GhosttyVtCellData.SemanticContent, &semantic),
            "read terminal-cell semantic role");

        var style = GhosttyVtStyle.CreateSized();
        if (hasStyling != 0)
        {
            EnsureSuccess(
                GhosttyVtNative.RenderRowCellsGet(
                    _rowCells,
                    GhosttyVtRenderCellData.Style,
                    &style),
                "read terminal-cell style");
        }

        var width = nativeWidth switch
        {
            GhosttyVtCellWide.Narrow => TerminalRenderCellWidth.Narrow,
            GhosttyVtCellWide.Wide => TerminalRenderCellWidth.Wide,
            GhosttyVtCellWide.SpacerTail => TerminalRenderCellWidth.SpacerTail,
            GhosttyVtCellWide.SpacerHead => TerminalRenderCellWidth.SpacerHead,
            _ => TerminalRenderCellWidth.Narrow,
        };
        var text = width is TerminalRenderCellWidth.SpacerHead
            or TerminalRenderCellWidth.SpacerTail
            ? string.Empty
            : ReadCellTextUnsafe();

        var foregroundColor = foregroundResult == GhosttyVtResult.Success
            ? ToCellColor(foreground)
            : TerminalCellColor.Default;
        var backgroundColor = backgroundResult == GhosttyVtResult.Success
            ? ToCellColor(background)
            : TerminalCellColor.Default;

        return new TerminalRenderCell(
            text,
            width,
            foregroundColor,
            backgroundColor,
            MapStyle(style, isProtected != 0),
            MapUnderline(style.Underline),
            ResolveUnderlineColor(style.UnderlineColor, colors),
            semantic switch
            {
                GhosttyVtCellSemanticContent.Input => TerminalCellSemanticRole.Input,
                GhosttyVtCellSemanticContent.Prompt => TerminalCellSemanticRole.Prompt,
                _ => TerminalCellSemanticRole.Output,
            },
            ReadHyperlinkUnsafe(rawCell, row, column),
            selected != 0);
    }

    private unsafe string ReadCellTextUnsafe()
    {
        Span<byte> stack = stackalloc byte[64];
        fixed (byte* pointer = stack)
        {
            var buffer = new GhosttyVtBuffer
            {
                Pointer = (nint)pointer,
                Capacity = checked((nuint)stack.Length),
                Length = 0,
            };
            var result = GhosttyVtNative.RenderRowCellsGet(
                _rowCells,
                GhosttyVtRenderCellData.GraphemesUtf8,
                &buffer);
            if (result == GhosttyVtResult.Success)
            {
                return buffer.Length == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(stack[..checked((int)buffer.Length)]);
            }

            if (result != GhosttyVtResult.OutOfSpace || buffer.Length > int.MaxValue)
            {
                EnsureSuccess(result, "read terminal-cell grapheme");
            }

            var bytes = new byte[checked((int)buffer.Length)];
            fixed (byte* retry = bytes)
            {
                buffer.Pointer = (nint)retry;
                buffer.Capacity = checked((nuint)bytes.Length);
                buffer.Length = 0;
                EnsureSuccess(
                    GhosttyVtNative.RenderRowCellsGet(
                        _rowCells,
                        GhosttyVtRenderCellData.GraphemesUtf8,
                        &buffer),
                    "read terminal-cell grapheme");
            }

            return Encoding.UTF8.GetString(bytes, 0, checked((int)buffer.Length));
        }
    }

    private unsafe string? ReadHyperlinkUnsafe(ulong rawCell, int row, int column)
    {
        byte hasHyperlink = 0;
        EnsureSuccess(
            GhosttyVtNative.CellGet(
                rawCell,
                GhosttyVtCellData.HasHyperlink,
                &hasHyperlink),
            "read terminal-cell hyperlink marker");
        if (hasHyperlink == 0)
        {
            return null;
        }

        var reference = GetViewportGridReferenceUnsafe(column, row);
        nuint required = 0;
        var result = GhosttyVtNative.GridRefHyperlinkUri(
            &reference,
            null,
            0,
            &required);
        if (result == GhosttyVtResult.NoValue)
        {
            return null;
        }

        if (result != GhosttyVtResult.OutOfSpace || required == 0 || required > int.MaxValue)
        {
            EnsureSuccess(result, "measure terminal hyperlink");
        }

        var bytes = new byte[checked((int)required)];
        fixed (byte* output = bytes)
        {
            EnsureSuccess(
                GhosttyVtNative.GridRefHyperlinkUri(
                    &reference,
                    output,
                    checked((nuint)bytes.Length),
                    &required),
                "read terminal hyperlink");
        }

        return Encoding.UTF8.GetString(bytes, 0, checked((int)required));
    }

    private unsafe TerminalRenderCursor ReadRenderCursorUnsafe(GhosttyVtRenderStateColors* colors)
    {
        GhosttyVtRenderCursorStyle visual = GhosttyVtRenderCursorStyle.Block;
        byte visible = 0;
        byte blinking = 0;
        byte password = 0;
        byte inViewport = 0;
        byte wideTail = 0;
        ushort x = 0;
        ushort y = 0;
        EnsureSuccess(GhosttyVtNative.RenderStateGet(
            _renderState, GhosttyVtRenderStateData.CursorVisualStyle, &visual), "read cursor style");
        EnsureSuccess(GhosttyVtNative.RenderStateGet(
            _renderState, GhosttyVtRenderStateData.CursorVisible, &visible), "read cursor visibility");
        EnsureSuccess(GhosttyVtNative.RenderStateGet(
            _renderState, GhosttyVtRenderStateData.CursorBlinking, &blinking), "read cursor blink");
        EnsureSuccess(GhosttyVtNative.RenderStateGet(
            _renderState, GhosttyVtRenderStateData.CursorPasswordInput, &password), "read cursor password state");
        EnsureSuccess(GhosttyVtNative.RenderStateGet(
            _renderState, GhosttyVtRenderStateData.CursorViewportHasValue, &inViewport), "read cursor viewport state");
        if (inViewport != 0)
        {
            EnsureSuccess(GhosttyVtNative.RenderStateGet(
                _renderState, GhosttyVtRenderStateData.CursorViewportX, &x), "read cursor column");
            EnsureSuccess(GhosttyVtNative.RenderStateGet(
                _renderState, GhosttyVtRenderStateData.CursorViewportY, &y), "read cursor row");
            EnsureSuccess(GhosttyVtNative.RenderStateGet(
                _renderState, GhosttyVtRenderStateData.CursorViewportWideTail, &wideTail), "read cursor wide-tail state");
        }

        return new TerminalRenderCursor(
            visual switch
            {
                GhosttyVtRenderCursorStyle.Bar => TerminalCursorVisualStyle.Bar,
                GhosttyVtRenderCursorStyle.Underline => TerminalCursorVisualStyle.Underline,
                GhosttyVtRenderCursorStyle.HollowBlock => TerminalCursorVisualStyle.HollowBlock,
                _ => TerminalCursorVisualStyle.Block,
            },
            visible != 0,
            blinking != 0,
            password != 0,
            inViewport != 0 ? y : null,
            inViewport != 0 ? x : null,
            wideTail != 0,
            colors->CursorHasValue != 0 ? ToCellColor(colors->Cursor) : null);
    }

    private unsafe TerminalKittyGraphicsFrame ReadKittyGraphicsUnsafe()
    {
        nint graphics = 0;
        var graphicsResult = GhosttyVtNative.TerminalGet(
            _terminal,
            GhosttyVtTerminalData.KittyGraphics,
            &graphics);
        if (graphicsResult == GhosttyVtResult.NoValue || graphics == 0)
        {
            _kittyImages.Clear();
            return TerminalKittyGraphicsFrame.Empty;
        }

        EnsureSuccess(graphicsResult, "read Kitty image storage");
        ulong storageGeneration = 0;
        EnsureSuccess(
            GhosttyVtNative.KittyGraphicsGet(
                graphics,
                GhosttyVtKittyGraphicsData.Generation,
                &storageGeneration),
            "read Kitty storage generation");

        var iterator = _kittyPlacementIterator.DangerousGetHandle();
        EnsureSuccess(
            GhosttyVtNative.KittyGraphicsGet(
                graphics,
                GhosttyVtKittyGraphicsData.PlacementIterator,
                &iterator),
            "populate Kitty placement iterator");

        var placements = new List<TerminalKittyPlacement>();
        var referencedImages = new Dictionary<TerminalKittyImageKey, TerminalKittyImageContent>();
        long referencedImageBytes = 0;
        while (GhosttyVtNative.KittyPlacementNext(_kittyPlacementIterator))
        {
            uint imageId = 0;
            uint placementId = 0;
            byte isVirtual = 0;
            int zIndex = 0;
            uint xOffset = 0;
            uint yOffset = 0;
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.ImageId, &imageId);
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.PlacementId, &placementId);
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.IsVirtual, &isVirtual);
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.Z, &zIndex);
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.XOffset, &xOffset);
            ReadKittyPlacementValue(GhosttyVtKittyPlacementData.YOffset, &yOffset);

            if (isVirtual != 0)
            {
                // Stored virtual placement definitions do not represent visible
                // placeholder instances. Ghostty's virtual iterator below
                // resolves those instances using its canonical renderer logic.
                continue;
            }

            var image = GhosttyVtNative.KittyGraphicsImage(graphics, imageId);
            if (image == 0)
            {
                continue;
            }

            var info = GhosttyVtKittyPlacementRenderInfo.CreateSized();
            var infoResult = GhosttyVtNative.KittyPlacementRenderInfo(
                _kittyPlacementIterator,
                image,
                _terminal,
                &info);
            if (infoResult is not (GhosttyVtResult.Success or GhosttyVtResult.NoValue))
            {
                EnsureSuccess(infoResult, "read Kitty placement geometry");
            }

            // A placement outside the viewport has no drawable geometry. Do not
            // copy its decoded image payload into managed memory until Ghostty's
            // canonical placement calculation says it is actually visible.
            if (infoResult == GhosttyVtResult.NoValue || info.ViewportVisible == 0)
            {
                continue;
            }

            var content = ReadKittyImageUnsafe(image);
            ReferenceKittyImage(content, referencedImages, ref referencedImageBytes);

            var sourceWidth = info.SourceWidth == 0
                ? checked((uint)content.PixelWidth)
                : info.SourceWidth;
            var sourceHeight = info.SourceHeight == 0
                ? checked((uint)content.PixelHeight)
                : info.SourceHeight;
            var source = new TerminalKittySourceRectangle(
                checked((int)info.SourceX),
                checked((int)info.SourceY),
                checked((int)sourceWidth),
                checked((int)sourceHeight));
            var geometry = new TerminalKittyPlacementGeometry(
                info.ViewportColumn,
                info.ViewportRow,
                checked((int)info.GridColumns),
                checked((int)info.GridRows),
                ToLogicalSize(info.PixelWidth),
                ToLogicalSize(info.PixelHeight));

            placements.Add(new TerminalKittyPlacement(
                content.Key,
                placementId,
                IsVirtual: false,
                zIndex,
                ToLogicalOffset(xOffset),
                ToLogicalOffset(yOffset),
                source,
                geometry));
        }

        ReadKittyVirtualPlacementsUnsafe(
            graphics,
            placements,
            referencedImages,
            ref referencedImageBytes);

        _kittyImages.Clear();
        foreach (var (key, image) in referencedImages)
        {
            _kittyImages[key] = image;
        }

        return placements.Count == 0
            ? new TerminalKittyGraphicsFrame(storageGeneration)
            : new TerminalKittyGraphicsFrame(
                storageGeneration,
                placements,
                [.. referencedImages.Values]);
    }

    private unsafe void ReadKittyVirtualPlacementsUnsafe(
        nint graphics,
        List<TerminalKittyPlacement> placements,
        Dictionary<TerminalKittyImageKey, TerminalKittyImageContent> referencedImages,
        ref long referencedImageBytes)
    {
        var resetResult = GhosttyVtNative.KittyVirtualPlacementIteratorReset(
            _kittyVirtualPlacementIterator,
            _terminal,
            _cellWidthPixels,
            _cellHeightPixels);
        if (resetResult == GhosttyVtResult.NoValue)
        {
            return;
        }

        EnsureSuccess(resetResult, "reset Kitty virtual-placement iterator");
        while (true)
        {
            var info = GhosttyVtKittyVirtualPlacementRenderInfo.CreateSized();
            var result = GhosttyVtNative.KittyVirtualPlacementNext(
                _kittyVirtualPlacementIterator,
                &info);
            if (result == GhosttyVtResult.NoValue)
            {
                return;
            }

            EnsureSuccess(result, "read Kitty virtual placement");
            var image = GhosttyVtNative.KittyGraphicsImage(graphics, info.ImageId);
            if (image == 0
                || info.SourceWidth == 0
                || info.SourceHeight == 0
                || info.DestinationWidth == 0
                || info.DestinationHeight == 0)
            {
                continue;
            }

            var content = ReadKittyImageUnsafe(image);
            ReferenceKittyImage(content, referencedImages, ref referencedImageBytes);
            var gridColumns = Math.Max(
                1,
                DivideRoundUp(
                    checked(info.CellOffsetX + info.DestinationWidth),
                    _cellWidthPixels));
            var gridRows = Math.Max(
                1,
                DivideRoundUp(
                    checked(info.CellOffsetY + info.DestinationHeight),
                    _cellHeightPixels));
            placements.Add(new TerminalKittyPlacement(
                content.Key,
                info.PlacementId,
                IsVirtual: true,
                info.ZIndex,
                ToLogicalOffset(info.CellOffsetX),
                ToLogicalOffset(info.CellOffsetY),
                new TerminalKittySourceRectangle(
                    checked((int)info.SourceX),
                    checked((int)info.SourceY),
                    checked((int)info.SourceWidth),
                    checked((int)info.SourceHeight)),
                new TerminalKittyPlacementGeometry(
                    info.ViewportColumn,
                    info.ViewportRow,
                    checked((int)gridColumns),
                    checked((int)gridRows),
                    ToLogicalSize(info.DestinationWidth),
                    ToLogicalSize(info.DestinationHeight))));
        }
    }

    private double ToLogicalOffset(uint physicalPixels) =>
        physicalPixels / _renderScale;

    private double ToLogicalSize(uint physicalPixels) =>
        Math.Max(1 / _renderScale, ToLogicalOffset(physicalPixels));

    private static int DivideRoundUp(uint value, uint divisor) =>
        checked((int)((value + divisor - 1) / divisor));

    private unsafe TerminalKittyImageContent ReadKittyImageUnsafe(nint image)
    {
        uint imageId = 0;
        uint imageNumber = 0;
        uint width = 0;
        uint height = 0;
        GhosttyVtKittyImageFormat format = GhosttyVtKittyImageFormat.Rgba;
        nint data = 0;
        nuint dataLength = 0;
        ulong generation = 0;
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Id, &imageId);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Number, &imageNumber);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Width, &width);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Height, &height);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Format, &format);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.DataPointer, &data);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.DataLength, &dataLength);
        ReadKittyImageValue(image, GhosttyVtKittyImageData.Generation, &generation);

        var key = new TerminalKittyImageKey(imageId, generation);
        if (_kittyImages.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (data == 0
            || dataLength > int.MaxValue
            || (ulong)dataLength > KittyImageStorageLimit)
        {
            throw new InvalidOperationException("Kitty image storage returned invalid pixel data.");
        }

        var pixels = new byte[checked((int)dataLength)];
        new ReadOnlySpan<byte>((void*)data, pixels.Length).CopyTo(pixels);
        return TerminalKittyImageContent.FromOwnedPixels(
            key,
            imageNumber,
            checked((int)width),
            checked((int)height),
            format switch
            {
                GhosttyVtKittyImageFormat.Rgb => TerminalKittyImagePixelFormat.Rgb,
                GhosttyVtKittyImageFormat.Rgba => TerminalKittyImagePixelFormat.Rgba,
                GhosttyVtKittyImageFormat.GrayAlpha => TerminalKittyImagePixelFormat.GrayAlpha,
                GhosttyVtKittyImageFormat.Gray => TerminalKittyImagePixelFormat.Gray,
                _ => throw new InvalidOperationException(
                    "Kitty image storage returned an undecoded image format."),
            },
            pixels);
    }

    private static void ReferenceKittyImage(
        TerminalKittyImageContent image,
        Dictionary<TerminalKittyImageKey, TerminalKittyImageContent> referencedImages,
        ref long referencedImageBytes)
    {
        if (referencedImages.ContainsKey(image.Key))
        {
            return;
        }

        referencedImageBytes = checked(referencedImageBytes + image.Pixels.Length);
        if ((ulong)referencedImageBytes > KittyImageStorageLimit)
        {
            throw new InvalidOperationException(
                "Visible Kitty image data exceeds the configured terminal storage bound.");
        }

        referencedImages.Add(image.Key, image);
    }

    private unsafe void ReadKittyPlacementValue(GhosttyVtKittyPlacementData data, void* output) =>
        EnsureSuccess(
            GhosttyVtNative.KittyPlacementGet(_kittyPlacementIterator, data, output),
            $"read Kitty placement {data}");

    private static unsafe void ReadKittyImageValue(
        nint image,
        GhosttyVtKittyImageData data,
        void* output) =>
        EnsureSuccess(
            GhosttyVtNative.KittyGraphicsImageGet(image, data, output),
            $"read Kitty image {data}");

    private unsafe TerminalScreenSnapshot BuildScreenSnapshotUnsafe(TerminalRenderFrame frame)
    {
        var capturedRowCount = Math.Min(
            frame.Rows,
            Math.Max(1, MaximumCapturedCells / Math.Max(1, frame.Columns)));
        var rows = new List<TerminalScreenRow>(capturedRowCount);
        var text = new StringBuilder(Math.Min(
            MaximumPlainTextCharacters,
            capturedRowCount * Math.Min(frame.Columns, 256)));
        var truncated = capturedRowCount < frame.Rows;
        for (var rowIndex = 0; rowIndex < capturedRowCount; rowIndex++)
        {
            var renderRow = frame.ViewportRows[rowIndex];
            var screenCells = new List<TerminalScreenCell>(renderRow.Cells.Count);
            var line = new StringBuilder();
            foreach (var cell in renderRow.Cells)
            {
                screenCells.Add(new TerminalScreenCell(
                    cell.Text,
                    cell.Width switch
                    {
                        TerminalRenderCellWidth.Wide => 2,
                        TerminalRenderCellWidth.SpacerHead or TerminalRenderCellWidth.SpacerTail => 0,
                        _ => 1,
                    },
                    cell.Foreground,
                    cell.Background,
                    MapScreenStyle(cell),
                    cell.Hyperlink,
                    cell.IsSelected));
                if (cell.Width is not (
                    TerminalRenderCellWidth.SpacerHead or TerminalRenderCellWidth.SpacerTail))
                {
                    line.Append(cell.Text.Length == 0 ? ' ' : cell.Text);
                }
            }

            // Ghostty's row wrap bit means this physical row continues into the
            // next one. Preserve the complete row in that case and do not inject
            // an artificial newline into the logical text exposed to automation.
            // Hard-ended rows may still discard their unused terminal padding.
            var lineText = renderRow.IsWrapped
                ? line.ToString()
                : line.ToString().TrimEnd();
            if (text.Length + lineText.Length + 1 > MaximumPlainTextCharacters)
            {
                var remaining = Math.Max(0, MaximumPlainTextCharacters - text.Length);
                text.Append(lineText.AsSpan(0, Math.Min(remaining, lineText.Length)));
                truncated = true;
            }
            else
            {
                text.Append(lineText);
            }

            rows.Add(new TerminalScreenRow(rowIndex, screenCells, renderRow.IsWrapped));
            if (!renderRow.IsWrapped
                && rowIndex + 1 < capturedRowCount
                && text.Length < MaximumPlainTextCharacters)
            {
                text.Append('\n');
            }
        }

        GhosttyVtTerminalScreen activeScreen = GhosttyVtTerminalScreen.Primary;
        byte mouseTracking = 0;
        var scrollbar = new GhosttyVtTerminalScrollbar();
        var title = new GhosttyVtString();
        var workingDirectory = new GhosttyVtString();
        EnsureSuccess(GhosttyVtNative.TerminalGet(
            _terminal, GhosttyVtTerminalData.ActiveScreen, &activeScreen), "read active screen");
        EnsureSuccess(GhosttyVtNative.TerminalGet(
            _terminal, GhosttyVtTerminalData.MouseTracking, &mouseTracking), "read mouse tracking");
        EnsureSuccess(GhosttyVtNative.TerminalGet(
            _terminal, GhosttyVtTerminalData.Scrollbar, &scrollbar), "read terminal scrollbar");
        EnsureSuccess(GhosttyVtNative.TerminalGet(
            _terminal, GhosttyVtTerminalData.Title, &title), "read terminal title");
        EnsureSuccess(GhosttyVtNative.TerminalGet(
            _terminal, GhosttyVtTerminalData.WorkingDirectory, &workingDirectory), "read terminal working directory");

        var cursorRow = Math.Clamp(frame.Cursor.ViewportRow ?? 0, 0, Math.Max(0, frame.Rows - 1));
        var cursorColumn = Math.Clamp(frame.Cursor.ViewportColumn ?? 0, 0, Math.Max(0, frame.Columns - 1));
        var above = checked((int)Math.Min(scrollbar.Offset, int.MaxValue));
        var remainingBelow = scrollbar.Total > scrollbar.Offset + scrollbar.Length
            ? scrollbar.Total - scrollbar.Offset - scrollbar.Length
            : 0;
        var below = checked((int)Math.Min(remainingBelow, int.MaxValue));
        var capturedAtUtc = DateTimeOffset.UtcNow;
        if (_interactiveState is { } interactiveState
            && interactiveState.ExpiresAtUtc <= capturedAtUtc)
        {
            _interactiveState = null;
        }

        return new TerminalScreenSnapshot(
            text.ToString().TrimEnd('\n'),
            cursorRow,
            cursorColumn,
            frame.Rows,
            frame.Columns,
            activeScreen == GhosttyVtTerminalScreen.Alternate,
            NullIfEmpty(workingDirectory.CopyUtf8()) ?? _launch.WorkingDirectory,
            capturedAtUtc,
            truncated,
            rows,
            ModeEnabledUnsafe(2004),
            mouseTracking != 0,
            _contentRevision,
            NullIfEmpty(title.CopyUtf8()),
            frame.Cursor.IsVisible && frame.Cursor.IsInViewport,
            above,
            below,
            BuildVisibleCommandBoundaries(frame, activeScreen),
            [.. _semanticMarkers.Select(marker => marker.Event)],
            _interactiveState);
    }

    private unsafe IReadOnlyList<TerminalCommandBoundary> BuildVisibleCommandBoundaries(
        TerminalRenderFrame frame,
        GhosttyVtTerminalScreen activeScreen)
    {
        var boundaries = new List<TerminalCommandBoundary>();
        foreach (var marker in _semanticMarkers)
        {
            if (marker.Screen != activeScreen || marker.Reference is null)
            {
                continue;
            }

            var point = new GhosttyVtPointCoordinate();
            var result = GhosttyVtNative.TrackedGridRefPoint(
                marker.Reference,
                GhosttyVtPointTag.Viewport,
                &point);
            if (result == GhosttyVtResult.NoValue)
            {
                continue;
            }

            EnsureSuccess(result, "resolve shell-integration marker");
            if (point.Y >= frame.Rows || point.X >= frame.Columns)
            {
                continue;
            }

            boundaries.Add(new TerminalCommandBoundary(
                marker.Event.Sequence,
                marker.Event.Kind,
                checked((int)point.Y),
                point.X,
                marker.Event.ExitCode));
        }

        return boundaries;
    }

    private static TerminalRenderCell CreateBlankCell() =>
        new(
            string.Empty,
            TerminalRenderCellWidth.Narrow,
            TerminalCellColor.Default,
            TerminalCellColor.Default);

    private static unsafe bool ReadRowFlag(ulong row, GhosttyVtRowData data)
    {
        byte value = 0;
        EnsureSuccess(GhosttyVtNative.RowGet(row, data, &value), $"read terminal-row {data}");
        return value != 0;
    }

    private static unsafe TerminalRowSemanticRole ReadRowSemanticRole(ulong row)
    {
        GhosttyVtRowSemanticPrompt semantic = GhosttyVtRowSemanticPrompt.None;
        EnsureSuccess(
            GhosttyVtNative.RowGet(row, GhosttyVtRowData.SemanticPrompt, &semantic),
            "read terminal-row semantic role");
        return semantic switch
        {
            GhosttyVtRowSemanticPrompt.Prompt => TerminalRowSemanticRole.Prompt,
            GhosttyVtRowSemanticPrompt.PromptContinuation =>
                TerminalRowSemanticRole.PromptContinuation,
            _ => TerminalRowSemanticRole.None,
        };
    }

    private static unsafe TerminalRenderCellStyle MapStyle(GhosttyVtStyle style, bool isProtected)
    {
        var result = TerminalRenderCellStyle.None;
        if (style.Bold != 0)
        {
            result |= TerminalRenderCellStyle.Bold;
        }

        if (style.Faint != 0)
        {
            result |= TerminalRenderCellStyle.Faint;
        }

        if (style.Italic != 0)
        {
            result |= TerminalRenderCellStyle.Italic;
        }

        if (style.Blink != 0)
        {
            result |= TerminalRenderCellStyle.Blink;
        }

        if (style.Inverse != 0)
        {
            result |= TerminalRenderCellStyle.Inverse;
        }

        if (style.Invisible != 0)
        {
            result |= TerminalRenderCellStyle.Invisible;
        }

        if (style.Strikethrough != 0)
        {
            result |= TerminalRenderCellStyle.Strikethrough;
        }

        if (style.Overline != 0)
        {
            result |= TerminalRenderCellStyle.Overline;
        }

        if (isProtected)
        {
            result |= TerminalRenderCellStyle.Protected;
        }

        return result;
    }

    private static unsafe TerminalCellStyle MapScreenStyle(TerminalRenderCell cell)
    {
        var result = TerminalCellStyle.None;
        if (cell.Style.HasFlag(TerminalRenderCellStyle.Bold))
        {
            result |= TerminalCellStyle.Bold;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Faint))
        {
            result |= TerminalCellStyle.Dim;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Italic))
        {
            result |= TerminalCellStyle.Italic;
        }

        if (cell.Underline != TerminalUnderlineKind.None)
        {
            result |= TerminalCellStyle.Underline;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Blink))
        {
            result |= TerminalCellStyle.Blink;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Inverse))
        {
            result |= TerminalCellStyle.Inverse;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Invisible))
        {
            result |= TerminalCellStyle.Invisible;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Strikethrough))
        {
            result |= TerminalCellStyle.Strikethrough;
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Overline))
        {
            result |= TerminalCellStyle.Overline;
        }

        return result;
    }

    private static unsafe TerminalUnderlineKind MapUnderline(GhosttyVtUnderlineStyle underline) =>
        underline switch
        {
            GhosttyVtUnderlineStyle.Single => TerminalUnderlineKind.Single,
            GhosttyVtUnderlineStyle.Double => TerminalUnderlineKind.Double,
            GhosttyVtUnderlineStyle.Curly => TerminalUnderlineKind.Curly,
            GhosttyVtUnderlineStyle.Dotted => TerminalUnderlineKind.Dotted,
            GhosttyVtUnderlineStyle.Dashed => TerminalUnderlineKind.Dashed,
            _ => TerminalUnderlineKind.None,
        };

    private static unsafe TerminalCellColor? ResolveUnderlineColor(
        GhosttyVtStyleColor color,
        GhosttyVtRenderStateColors* colors) => color.Tag switch
        {
            GhosttyVtStyleColorTag.None => null,
            GhosttyVtStyleColorTag.Rgb => ToCellColor(color.Value.Rgb),
            GhosttyVtStyleColorTag.Palette => ToCellColor(new GhosttyVtColorRgb(
                colors->Palette[color.Value.Palette * 3],
                colors->Palette[color.Value.Palette * 3 + 1],
                colors->Palette[color.Value.Palette * 3 + 2])),
            _ => null,
        };

    private static unsafe TerminalCellColor ToCellColor(GhosttyVtColorRgb color) =>
        new(
            TerminalColorMode.Rgb,
            color.Red << 16 | color.Green << 8 | color.Blue);

    private static unsafe string? NullIfEmpty(string value) =>
        value.Length == 0 ? null : value;
}
