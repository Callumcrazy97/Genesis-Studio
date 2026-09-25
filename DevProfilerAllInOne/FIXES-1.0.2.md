# DevProfiler 1.0.2 dark-theme correction

This revision replaces the remaining Windows light-theme control templates that made parts of the interface unreadable.

## Corrected controls

- Language ComboBox and dropdown items
- TabControl header strip, selected tab, hover state, and content surface
- DataGrid body, headers, rows, selected rows, and empty space
- TextBox focus, hover, selection, disabled state, and multiline raw output
- Button hover, pressed, focus, and disabled states
- Vertical and horizontal scrollbars
- Tooltips and splitters

## Root cause

The earlier theme changed colours on controls but still used native WPF templates. Those templates continued to render several white Windows surfaces. A global implicit `TextBlock` foreground also overrode the foreground inherited from ComboBox and TabItem, producing white text on those white surfaces.

Version 1.0.2 removes that foreground override and supplies complete dark templates for the affected controls.
