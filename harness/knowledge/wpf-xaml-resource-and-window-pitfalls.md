# WPF XAML Resource Scope & Window Lifecycle Pitfalls

> Owner: **`code-coach`** (primary) — these are the patterns to flag during pre-commit
> review of XAML / Window code-behind diffs.
> Cross-reference: **`build-doctor`** (secondary) — XAML errors look like build errors
> in Debug runs; same root causes.

This is a record of WPF traps that crashed AgentZero Lite during the
2026-04-29 voice-pipeline build. Every entry below has a real
DispatcherUnhandled stack and a concrete fix in the project history. The
intent is to **catch these in code-coach review before they ship**, not to
re-derive them on every UI iteration.

---

## Pitfall 1 — `<StackPanel.Resources>` style ≠ window-scope style

**Symptom**

```
System.Windows.Markup.XamlParseException
  ---> System.Exception: 이름이 'MiniKeyButton'인 리소스를 찾을 수 없습니다.
```

at `Window.InitializeComponent()` → BAML load. Reproducible by clicking
the button that opens the offending Window (sidebar AgentBot click,
in the original repro).

**Root cause**

XAML `<X.Resources>` is **lexically scoped to that element's subtree**.
A style declared in `<StackPanel.Resources>` is visible to the buttons
*inside that StackPanel only*. Any sibling container (or a child of a
sibling) that does `Style="{StaticResource MiniKeyButton}"` will fail at
load time — the resource resolver walks up from the consumer through
parents, but it never enters a sibling's resource dictionary.

In `AgentBotWindow.xaml`, `MiniKeyButton` is defined inside the toolbar
StackPanel (lines 281–318 at the time of writing). New controls placed
in `pnlVoiceTestStrip` (a sibling Border on the same Grid) cannot see it.

**Two valid fixes**

1. **Inline-style the new control** like `btnVoiceCancelInflight` does
   (Background / BorderBrush / Foreground / Padding / Cursor inline).
   Lowest-impact for one-off additions.
2. **Promote the style to `<Window.Resources>`** if it's going to be
   reused in multiple subtrees. Larger blast radius — touches the whole
   window — so weigh against (1).

**Pre-commit check**

When a XAML diff introduces a new `Style="{StaticResource X}"` reference,
`grep -nE 'x:Key="X"' <samefile>` and confirm the definition is in a
container that is a *parent* of the consumer, not a sibling.

---

## Pitfall 2 — Resource keys you assumed exist often don't

**Symptom**

```
System.Windows.Markup.XamlParseException
  ---> System.Exception: 이름이 'TextLight'인 리소스를 찾을 수 없습니다.
```

Same window-load BAML failure as Pitfall 1, but the resource was *never
defined anywhere* in this window's resource dictionary.

**Root cause**

Memory-from-other-codebases bias. WPF apps frequently define keys like
`TextLight` / `TextNormal` / `TextSecondary` — but **AgentBotWindow.xaml
defines exactly `TextPrimary` and `TextDim`, full stop**. Authoring code
on autopilot from a generic dark-theme template will reach for keys that
aren't there.

**The actual key inventory in `AgentBotWindow.xaml` (as of 2026-04-29)**

| Category | Keys |
|---|---|
| Backgrounds | `BgDeep`, `BgPanel`, `BgToolbar` |
| Borders | `BorderDim`, `InputBorder` |
| Text | `TextPrimary`, `TextDim` |
| Inputs | `InputBg` |
| Accents | `CyanBrush`, `PurpleBrush` |

There is **no** `TextLight`, `TextNormal`, `BgInput`, `BorderColor`, or
`Accent` in this window. Other windows in the codebase (`MainWindow`,
`CliDefEditWindow`, `NoteWindow`) have *different* inventories — don't
copy keys across windows.

**Pre-commit check**

`grep -E 'x:Key="(Bg|Border|Text|Input|Cyan|Purple)\w*"'` on the same
XAML file. If the new `StaticResource X` doesn't match any defined key,
fix before committing.

---

## Pitfall 3 — `Window.Resources` does NOT inherit from `Owner`

**Symptom**

A new `Window` subclass works fine when tested in isolation but crashes
on first show with the same `'X' 리소스를 찾을 수 없습니다` shape — even
though the consumer is *inside* the new Window, not the parent.

**Root cause**

`Window.Owner` controls z-order + activation, **not** resource resolution.
Each top-level Window starts a fresh resource lookup chain — the chain
walks from the consumer up through the Window's own subtree, then to
`Application.Current.Resources`, then implicit theme resources. **It
never visits the Owner window's resource dictionary.**

This bites when you split a feature out of `AgentBotWindow.xaml` into a
new popup (e.g. `TestToolsWindow.xaml`) and reuse the same StaticResource
keys without copying the brush definitions over.

**Fix**

Every new Window must declare its own `<Window.Resources>` (or pull from
`App.xaml`'s `<Application.Resources>`). Either:

1. **Self-contain** — duplicate the brush keys you need (cheap, isolated).
   This is what `TestToolsWindow.xaml` does.
2. **Lift to `App.xaml`** — single source of truth for brushes, every
   Window inherits via the application-level resource chain. Larger
   change; only worth it once you have 4+ Windows reusing the same keys.

**Pre-commit check**

When a new `Window` `.xaml` file appears, confirm its
`<Window.Resources>` declares every StaticResource key its own content
references. Don't trust that "Owner has it."

---

## Pitfall 4 — `Window.Owner = this` requires `this` to have an HWND

**Symptom**

```
System.InvalidOperationException:
  Owner 속성을 이전에 표시되지 않은 Window로 설정할 수 없습니다.
   at System.Windows.Window.set_Owner(Window value)
```

Stack origin: an event handler on a Window that is *not* `Show()`'n.

**Root cause specific to AgentBot**

`AgentBotWindow` has two display modes:

| Mode | How it's shown | Has HWND? |
|---|---|---|
| Floating | `_botWindow.Show()` | yes |
| Embedded | `EmbedBot()` calls `DetachContent()` to lift the visual tree into `MainWindow.BotDockHost`, then `_botWindow.Hide()` | **no** — the window's HWND was either never created (if first paint went straight to embedded) or was destroyed |

In embedded mode the user can still click buttons — those events route
through the visual tree which is now hosted by MainWindow — and the
event handler runs with `this == AgentBotWindow`. But `this` was never
shown as a top-level window, so it has no HWND, so
`new TestToolsWindow { Owner = this }` throws.

**Fix — `ResolveVisibleOwner()` pattern**

```csharp
private Window? ResolveVisibleOwner()
{
    if (IsVisible) return this;                      // floating mode
    var mw = Application.Current?.MainWindow;
    if (mw is not null && mw.IsVisible) return mw;   // embedded → host
    return Application.Current?.Windows
        .OfType<Window>()
        .FirstOrDefault(w => w.IsVisible && w.IsLoaded);
}
```

Then assign Owner only if non-null — fall back to no-Owner instead of
crashing. The popup just won't follow that window's z-order; it still
opens.

**Pre-commit check**

Any `new Window() { Owner = this }` or `popup.Owner = this` in code-behind
of a Window that has *any* embed/dock/hide path needs a visibility check
or an `IsLoaded` guard. Search: `grep -nE '\.Owner\s*=' src/**/*.cs`.

---

## Pitfall 5 — `System.Drawing.Brushes` vs `System.Windows.Media.Brushes`

**Symptom**

```
error CS0104: 'Brush'은(는) 'System.Drawing.Brush' 및
              'System.Windows.Media.Brush' 사이에 모호한 참조입니다.
```

Compile-time, on first `Brushes.OrangeRed` reference in a new file.

**Root cause project-specific**

`AgentZeroWpf.csproj` has both `<UseWPF>true</UseWPF>` AND
`<UseWindowsForms>true</UseWindowsForms>`. Both pull in `System.Drawing`
and `System.Windows.Media`, both of which export `Brush` and `Brushes`.

Worse, `Project/AgentZeroWpf/GlobalUsings.cs` already declares
`global using Brushes = System.Windows.Media.Brushes;` — but **only
`Brushes`, not `Brush`**. So `Brushes.OrangeRed` resolves cleanly
(global alias picked) while `(Brush)FindResource("X")` fails to compile.

**Fix**

Pick one of:

1. Add a per-file `using Brush = System.Windows.Media.Brush;` at the top
   of the new code-behind. (What `TestToolsWindow.xaml.cs` does.)
2. Fully qualify inline: `(System.Windows.Media.Brush)FindResource(...)`.
   Verbose but no top-of-file dependency. (What `AgentBotWindow.Voice.cs`
   does.)
3. Add `Brush` to `GlobalUsings.cs` if you find yourself doing this in
   every new file. Larger change — touches every project file.

Don't double-declare `Brushes` in a new file; the global alias already
covers it. Compiler error: `using 별칭 'Brushes'을(를) 이전에 이
네임스페이스에서 사용했습니다`.

**Pre-commit check**

`grep -nE '^using.*Brush(es)?\s*=' <new-file>` and cross-check against
`GlobalUsings.cs` to avoid both the missing alias and the duplicate.

---

## Pitfall 6 — Default WPF controls bypass `Background` / `Foreground` in dark mode

**Symptom**

You set `Background="#0F0F22"` and `Foreground="#E5E5E5"` on a `<ComboBox>` /
`<ListBox>` / `<TabControl>` / `<ScrollBar>` inside an otherwise-dark window
and the result still has system-color chrome — a white dropdown popup, a
white-on-white selected item, gray ToggleButton chevron blocks. No XAML
warning. The window looks broken on first open.

**Why**

Default WPF control templates wrap multiple inner parts (`ToggleButton`,
`Popup`, `ContentPresenter`, `ScrollViewer`, item containers). Most of those
parts read from `SystemColors` (e.g. `Window`, `WindowText`, `Highlight`,
`HighlightText`) — *not* the host control's `Background` / `Foreground`.
Setting one color on the outer control reaches the outermost border only.

This bites repeatedly on this project because every dialog ships its own
dark palette and we keep writing `<ComboBox Background="..." Foreground="..."/>`
without remembering it doesn't work.

**Fix — ship a full re-template, not just colors**

Required parts when re-templating a `ComboBox`:

```xml
<!-- 1) ComboBoxItem template — controls highlight + selected colors -->
<Style x:Key="DarkComboBoxItem" TargetType="ComboBoxItem">
    <Setter Property="Background" Value="#1B1B1C"/>
    <Setter Property="Foreground" Value="#E5E5E5"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ComboBoxItem">
                <Border x:Name="Bd" Background="{TemplateBinding Background}" Padding="10,6">
                    <ContentPresenter TextElement.Foreground="{TemplateBinding Foreground}"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="#1A2A4D"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="#0A84FF"/>
                        <Setter Property="Foreground" Value="#FFFFFF"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- 2) ToggleButton template — the closed-state chevron + border -->
<Style x:Key="DarkComboBoxToggleStyle" TargetType="ToggleButton">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="ClickMode" Value="Press"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ToggleButton">
                <Border x:Name="Bd" Background="#0F0F22" BorderBrush="#2A2A5E" BorderThickness="1" CornerRadius="4">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/><ColumnDefinition Width="24"/>
                        </Grid.ColumnDefinitions>
                        <Path Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center"
                              Data="M 0 0 L 4 4 L 8 0 Z" Fill="#9D9D9D"/>
                    </Grid>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- 3) ComboBox template — wires ToggleButton, ContentPresenter (selection), Popup -->
<Style x:Key="DarkComboBox" TargetType="ComboBox">
    <Setter Property="Foreground" Value="#E5E5E5"/>
    <Setter Property="MinHeight" Value="32"/>
    <Setter Property="ItemContainerStyle" Value="{StaticResource DarkComboBoxItem}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ComboBox">
                <Grid>
                    <ToggleButton x:Name="ToggleButton"
                                  Style="{StaticResource DarkComboBoxToggleStyle}"
                                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay,
                                              RelativeSource={RelativeSource TemplatedParent}}"/>
                    <ContentPresenter IsHitTestVisible="False"
                                      Content="{TemplateBinding SelectionBoxItem}"
                                      ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                      ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"
                                      Margin="10,0,28,0" VerticalAlignment="Center"
                                      TextElement.Foreground="#E5E5E5"/>
                    <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
                           AllowsTransparency="True" Focusable="False" PopupAnimation="Slide">
                        <Border Background="#1B1B1C" BorderBrush="#2A2A5E" BorderThickness="1" CornerRadius="4"
                                MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                MaxHeight="{TemplateBinding MaxDropDownHeight}">
                            <ScrollViewer><StackPanel IsItemsHost="True"
                                            KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                        </Border>
                    </Popup>
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

The same shape applies to `ListBox` (item template + ScrollViewer in
template), `TabControl` (TabItem template), and `ScrollBar` (Track + Thumb
templates). **Setting Background/Foreground only is never enough — always
re-template.**

**Project palette (use these exact tokens)**

| Role | Hex |
|---|---|
| Window background | `#1B1B1C` |
| Card / panel background | `#171718` |
| Input background | `#0F0F22` |
| Border (idle) | `#2A2A5E` (input) / `#2A2A2D` (panel) / `#3F3F46` (button) |
| Border (hover/focus) | `#0A84FF` |
| Foreground (primary) | `#E5E5E5` |
| Foreground (muted) | `#9D9D9D` |
| Foreground (very muted) | `#6E6E73` |
| Item highlight (hover) | `#1A2A4D` |
| Item selected | `#0A84FF` (bg) + `#FFFFFF` (fg) |
| Accent (primary button) | `#0A84FF` |

When in doubt, copy from `InstallPluginPickerDialog.xaml` — it has the
canonical re-templates for ComboBox, primary/ghost button, and choice
button.

**Pre-commit check**

`grep -nE '<(ComboBox|ListBox|TabControl|ScrollBar|TreeView|DataGrid)\s' <new-xaml>`.
For each match, verify a `Style="{StaticResource Dark*}"` is set OR the
control sits in a Window resource scope that defines a TargetType-only
default style. **Plain `Background="..." Foreground="..."` on these
controls is a bug** — the dropdown / item highlight / chevron will all
revert to system colors.

---

## Cross-cutting: how these surface

All five share one operational pattern: **the user runs the app, clicks
a path that constructs a Window or its content, and a
DispatcherUnhandled crash dialog interrupts the session.** None of them
fail at compile time except #5.

That makes the build-green / tests-green signal *insufficient* — the
log file is the source of truth. `dotnet build` will say "0 errors" and
the app still crashes on first user click into the new UI.

**Where the log lives**

```
Project/AgentZeroWpf/bin/Debug/net10.0-windows/logs/app-log.txt
```

(BaseDirectory-relative; `AppLogger.EnableFileOutput(AppContext.BaseDirectory)`
in `App.xaml.cs:91`.) Not the user's `%LOCALAPPDATA%\AgentZeroWpf\logs\`
fallback — that path only kicks in if BaseDirectory is unwritable.

Tail with `tail -120 ... | grep -A 10 "CRASH\|XamlParseException"` after
any UI-touching change.

---

## Why this knowledge file exists

These five all surfaced in a single hour during the
`d11b3b5 → 585f8f3` chain (virtual voice injector → popup migration).
Each fix was a one-liner. Each crash blocked the user end-to-end.

The cost of catching them in code-coach Mode 2 review (one grep per
pitfall) is roughly zero. The cost of catching them in production is
"re-launch the app, re-trigger the path, re-read the stack." Bake the
checks into review.

## Pre-commit checklist for code-coach (XAML/Window diffs)

When a staged diff touches `*.xaml` or a Window code-behind, run these:

- [ ] **P1**: every new `StaticResource X` in XAML — grep `x:Key="X"` in
      the same file, confirm definition is in a *parent* container, not a
      sibling's `<Resources>`.
- [ ] **P2**: every new `StaticResource X` in XAML — confirm `X` is one
      of the keys actually defined in this file's resource dictionary
      (not assumed from another window).
- [ ] **P3**: every new `Window` subclass — confirm `<Window.Resources>`
      declares every key its content uses; do NOT rely on Owner inheritance.
- [ ] **P4**: every `new SomeWindow { Owner = this }` or
      `popup.Owner = this` — confirm the host window cannot be in a
      Hide()'n / never-shown state at the moment of the call. If it can,
      use a `ResolveVisibleOwner()`-shaped helper.
- [ ] **P5**: every new code-behind with `Brush` / `Brushes` — confirm
      no duplicate `using Brushes = ...` (already global) and add a
      `using Brush = System.Windows.Media.Brush;` if needed.
- [ ] **P6**: every new / modified `<ComboBox>`, `<ListBox>`, `<TabControl>`,
      `<ScrollBar>`, `<TreeView>`, `<DataGrid>` in a dark-themed Window —
      confirm a full re-template (Style targeting the control AND its item
      container) is applied. Plain `Background="..." Foreground="..."` is
      not enough; the popup/chrome/highlight stay on system colors. Use
      the canonical templates in `InstallPluginPickerDialog.xaml` or copy
      from this pitfall section.
