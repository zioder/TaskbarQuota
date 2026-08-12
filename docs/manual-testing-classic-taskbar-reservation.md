# Manual test: classic taskbar space reservation

This procedure verifies the visual behavior that unit tests cannot cover: whether a classic or restored
task switcher continues to respect TaskbarQuota's tray-side slot after enough uncombined task buttons are
opened.

## Test environment

Record these details with the result:

- Windows version and build.
- Taskbar implementation and version, if a shell customization is installed.
- Display scaling, resolution, taskbar alignment, UI language direction, and monitor count.
- Taskbar button combining setting.
- TaskbarQuota widget display mode.

For the original reproduction, use Windows 11 with StartAllBack and configure taskbar buttons to **Never
combine**. The automated reservation currently applies only to the default position on a left-to-right,
primary classic taskbar that exposes `TrayNotifyWnd` and the complete
`ReBarWindow32` → `MSTaskSwWClass` → `MSTaskListWClass` hierarchy.

## Crowded classic taskbar

1. Close every installed or previously compiled TaskbarQuota instance.
2. Start the patched build and choose **Reset position** from its tray menu.
3. Confirm that the widget settles immediately before the notification area, with a small gap, instead of
   at the far-left edge.
4. Confirm for at least ten seconds that Start, the task buttons, and the widget do not flicker, jump, or
   oscillate.
5. Open separate application windows until the taskbar is as full as the original reproduction. Browser
   windows with distinct titles make overlaps easy to identify.
6. Keep opening windows until the taskbar must reduce, clip, scroll, or overflow its task buttons.
7. Wait at least one watcher interval (two seconds). Confirm that no task-button icon, title, hover
   background, or progress indicator is painted below the widget.
8. Close the extra windows one at a time. Confirm that the widget remains stable and no permanent blank
   region appears.
9. Capture before/after screenshots with the same number and order of windows as the original report.

Expected result: TaskbarQuota remains immediately before the notification area. Only the right edge of the
classic `MSTaskSwWClass` container is shortened; Start must stay in place. The task buttons handle crowding
inside the reduced container instead of drawing below TaskbarQuota.

## Widget width, visibility, and dragging

Repeat the following while enough windows are open to make an incorrect reservation visible:

1. Switch between **Bars only**, **Percentages only**, and **Bars and percentages**. The reserved slot must
   follow the widget's actual width without overlapping the task buttons or notification area.
2. Hide and show the widget. Hiding must restore the task switcher's original width; showing must recreate
   the tray-side reservation.
3. Begin moving the widget from the tray menu and cancel with Escape. The reservation must be released
   while moving and restored at the default tray-side position after cancellation.
4. Complete a drag to a custom position. A custom position must remain usable and the automatic classic
   reservation must stay released.
5. Choose **Reset position**. The widget must return to the reserved tray-side slot.
6. Quit TaskbarQuota. No permanent blank region may remain in the taskbar.
7. Relaunch TaskbarQuota and restart Explorer once. The widget and reservation must be recreated without
   requiring a settings change.

## Native Windows 11 regression check

1. Disable the third-party taskbar customization and restart Explorer so the native Windows 11 taskbar is
   active.
2. Start TaskbarQuota, reset its position, and repeat the ordinary and crowded-window checks.
3. Exercise drag, widget width changes, hide/show, and quit.

Expected result: native Windows 11 placement is unchanged. The classic reservation is inactive because the
native taskbar does not expose the complete classic hierarchy.

## Additional coverage and known limits

- If available, repeat on a non-100% display scale.
- A secondary taskbar without `TrayNotifyWnd` follows the existing placement behavior and is not reserved.
- Right-to-left taskbars follow the existing placement behavior; the classic reservation is intentionally
  limited to shortening the right edge so it never moves Start.
- Custom widget positions do not resize the classic task switcher. Reset the position to test the automatic
  reserved slot.
- The visual result depends on how the active shell lays out and clips its task-button children. Passing the
  automated tests does not establish compatibility with a particular shell customization.

## Diagnostics and failure reporting

Review `%TEMP%\taskbarquota.log` for `Could not reserve the classic task switcher area`. If the shell resets
the task-switcher bounds after an interaction, allow one watcher interval (two seconds) and record whether
the reservation returns cleanly. Do not report the fix as validated for that environment if overlap,
flicker, taskbar movement, or oscillation remains.
