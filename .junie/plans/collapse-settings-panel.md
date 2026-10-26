---
sessionId: session-261026-235157-ubg0
---

# Technical Design

### Current Implementation
- `SettingsPanel.Columns` is a `Grid` with two `ColumnDefinition`s, both `GridLength(1, GridUnitType.Star)` — equal 50/50 split.
- `SceneColumn` (column 1) contains `ShaderPanel`, which is `Visibility.Collapsed` initially but still occupies 50% of the grid.
- `MainWindow.InitializeHubSize()` sets the window width to `768 * scale`.
- `MainWindow` handles `settings.SceneSelected` by calling `OpenCanvas()` and optionally `shaders.SelectScene(name)`.
- `SettingsPanel.SetScenes()` sets `shaders.Visibility` to `Visible` when a scene is selected, `Collapsed` otherwise.
- `canvasWindow.Dismissed` resets `canvasDismissed` and calls `RefreshScenePicker()`.

### Key Decisions
- **Column width approach**: Change the second column from `Star` to `GridLength(0)` (collapsed) initially, then set it back to `GridLength(1, GridUnitType.Star)` when a scene is selected. This is simpler than using `Auto` width and avoids measuring the collapsed `ShaderPanel`.
- **Left column stability**: When expanding, first set the left column to a fixed pixel width (its current `ActualWidth`), then expand the right column and resize the window. After the layout settles, restore the left column to `Star` sizing. This prevents the left column from shrinking during the transition.
- **Window resizing**: Call `AppWindow.ResizeClient()` after expanding the column to widen the window by the right column's desired width.

### Proposed Changes

**`SettingsPanel.cs`:**
- Add a `CollapseSceneColumn()` method that sets column 1's width to `GridLength(0)`.
- Add an `ExpandSceneColumn()` method that sets column 1's width to `GridLength(1, GridUnitType.Star)` and returns the target width.
- Add a `ColumnWidths` property or expose the `ColumnDefinitions` for `MainWindow` to manipulate.
- Call `CollapseSceneColumn()` in the constructor after initial setup.
- In `SetScenes()`, call `ExpandSceneColumn()` when a scene is selected, `CollapseSceneColumn()` when no scene.

**`MainWindow.cs`:**
- In `InitializeHubSize()`, reduce the initial width from `768 * scale` to approximately `384 * scale` (single column).
- In the `SceneSelected` handler, after `OpenCanvas()`, resize the window to accommodate the expanded right column — the left column stays at its current pixel width, the window widens.
- In `canvasWindow.Dismissed`, collapse the right column and resize the window narrower.

### File Structure
- Modified: `AEther.WinUI/SettingsPanel.cs` — add column collapse/expand methods, collapse on init.
- Modified: `AEther.WinUI/MainWindow.cs` — adjust initial width, handle window resize on scene select/dismiss.

# Delivery Steps

### ✓ Step 1: Add column collapse/expand to SettingsPanel
SettingsPanel gains the ability to collapse and expand the right column, and starts with it collapsed.
- In `SettingsPanel.cs`, change the second `ColumnDefinition` from `GridLength(1, GridUnitType.Star)` to `GridLength(0)` in the constructor.
- Add `internal void CollapseSceneColumn()` — sets column 1 width to `GridLength(0)`.
- Add `internal void ExpandSceneColumn()` — sets column 1 width to `GridLength(1, GridUnitType.Star)`.
- In `SetScenes()`, call `ExpandSceneColumn()` when `selected != null`, `CollapseSceneColumn()` when `selected == null`.

### ✓ Step 2: Adjust MainWindow initial size and resize on scene changes
MainWindow starts narrower and resizes when scenes are selected or dismissed.
- In `MainWindow.InitializeHubSize()`, reduce the initial width from `768 * scale` to `384 * scale` (single column).
- In the `SceneSelected` handler, after `OpenCanvas()`, pin the left column (`AudioColumn`) to its current `ActualWidth` as a fixed width, then resize the window wider to accommodate the expanded right column, and finally restore the left column to `Star` sizing.
- In `canvasWindow.Dismissed` handler, collapse the right column via `settings.CollapseSceneColumn()` and resize the window narrower (back to single-column width).
- Ensure `RefreshScenePicker()` coordinates with the column state so the layout is consistent when `canvasDismissed` changes.