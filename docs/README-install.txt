Mouse Jiggler
=============

A small tray utility that keeps Windows awake and optionally moves the pointer one
pixel and back while you are away.

Getting started
---------------

Open Mouse Jiggler from the Start menu, or double-click the tray icon. The app starts
stopped: choose Start when you are ready. Nothing happens until you do.

Right-click the tray icon for Start, Stop and Settings. Closing the Settings window
returns the app to the tray; it does not stop it. Use Exit to close the app entirely.

Stop stays in effect
--------------------

Stop lasts until you explicitly start again. It survives a schedule boundary, plugging
in or unplugging power, locking and unlocking, restarting, and signing back in. Saving a
setting cannot start a stopped app.

Where your settings live
------------------------

%LOCALAPPDATA%\MouseJiggler\settings.json

The installed and portable editions deliberately share this file, so moving between
them keeps your settings and your explicit Stop.

Portable edition
----------------

The portable edition does not register itself to start with Windows. You can switch
that on in Settings, which registers the folder the app is currently in. If you then
move or delete that folder, turn the setting off first, or Windows will keep trying to
start an app that is no longer there.

Limitations worth knowing
-------------------------

Keeping the computer awake and preventing an idle lock are not the same thing. With
pointer movement switched off, Windows may still lock an idle session. These controls
are requests to Windows rather than guarantees.

Source and releases: https://github.com/craigtrim/mouse-jiggler
Licence: MIT
