# ClickTo: Slideshow — Release notes

## 1.0.2 — 2026-11-21

### Improvements

- **Help.** A new **?** button beside About, or the F1 key, opens Help in its own window:
  getting started from File Explorer, supported files and why some are skipped, arranging
  slides, durations and transitions, music and the runtime bar, options, playing fullscreen,
  saving and reopening a project, keyboard shortcuts, and privacy. It is built into the app and
  works offline.
- **Drag photos and music in.** Drag files or folders from File Explorer onto the window to add
  them at the end of the show. A folder adds the photos and music directly inside it, the same
  as right-clicking it. An empty slide list now says you can drop them there, and offers
  **Choose files…** (photos and music together) as well as **Choose folder…**.
- **Progress when saving a PowerPoint.** Saving as PowerPoint now shows how far it has got, with
  a Cancel button, just like saving a video.
- **Clearer messages.** Saved files, skipped files and problems now appear as messages at the
  bottom of the window, each with its own close button, in the same style as our other apps.
- **More from The Bureau of Stuff.** The About box now lists our other apps, each linking to
  its Microsoft Store page. Nothing is fetched online: the list is built into the app.
- **Report a problem** now fills in your app version, Windows version and runtime version in
  the email for you, and explains how to attach the log file.
- **Rate the app.** After you save a video or PowerPoint, if nothing has gone wrong since the
  app opened, it may ask once (and only once) whether you would like to rate it in the
  Microsoft Store.

### Fixes

- **Save button.** Clicking Save itself used to do nothing; only its arrow worked. It now saves
  a video, and the arrow still offers PowerPoint and project.
- **Sort button.** Clicking Sort itself also did nothing. It now sorts again by the order you
  chose last (by name until you choose one).
- **Original resolution now works.** Saving a video or PowerPoint with Resolution set to
  Original used to fail with an error. It now uses the size of your largest photo (up to what
  video players accept), and every photo fits or fills that frame.
- **Ctrl+Z.** Ctrl+Z only undid anything while a slide in the grid had focus. It now undoes
  wherever you are in the window, the same as the Undo button.
- **Undo a drag.** Dragging a slide or a music track into a new place couldn't be undone. Undo
  now puts it back, one drag at a time.
- **Undo a slide's duration or transition.** Changing one slide's time or transition couldn't
  be undone. Undo now puts it back. Changes in Options, which apply to the whole show, still
  aren't undone.
- **Runtime bar after a slide's duration change.** Giving one slide its own time didn't update
  the show's total length at the bottom of the window. It now updates straight away.
- **Editing while saving.** While a video or PowerPoint is saving, the show can no longer be
  changed: editing, Undo, the options, Open, Save and Play are turned off until the save
  finishes or you cancel it. Before, they all looked ready, and Save did nothing.
- **Missing-extension advice.** When a photo needs a Microsoft Store extension, its message used
  to say to "reload this file", which the app has no way to do. It now says to remove the slide
  and add the photo again.
- **Windows' media controls.** After music had played, ClickTo: Slideshow appeared in Windows'
  media controls above the taskbar, whose buttons could pause the music while the slides
  carried on. It no longer appears there.

### Corrections

- **Fullscreen on a second monitor.** The 1.0.0 notes said you could play fullscreen on
  whichever monitor you choose. You can't choose: with more than one screen, the show plays on
  one that isn't your main screen, so the editor stays where it is.

## 1.0.0 — September 2026

First release on the Microsoft Store.

- **Right-click to start.** Select photos in File Explorer, and a music track too if you want
  one, right-click, and choose **Create slideshow**. Photos from several folders work as easily
  as photos from one, and nothing is imported or copied into a library.
- **Arrange your pictures.** Photos appear as large thumbnails in a grid. Drag them into order
  or move them with Ctrl and the arrow keys, rotate anything sideways, remove pictures you don't
  want, and undo up to twenty steps. Double-click a picture for a full-size preview.
- **Sensible starting order.** Pictures arrive in natural filename order (IMG_2 before
  IMG_10). You can instead sort by date taken or date modified, or reverse the order.
- **Timing and transitions.** Set how long each picture stays on screen, one at a time or all
  at once. Choose from seven transitions: a straight cut, a crossfade, a fade through black,
  or a push in any of four directions.
- **Music that fits.** A runtime bar shows the slideshow's length against the music's, so you
  can see at a glance whether they match. Loop the music to fill, trim it, or stretch the
  slides to fit the song exactly.
- **Play fullscreen** on whichever monitor you choose, while the editor stays on the other.
- **Three ways to save:**
  - an **MP4 video** that plays on phones, televisions and USB sticks;
  - an editable **PowerPoint** file with your pictures, timings, transitions and music all
    embedded;
  - a **project file**, so you can come back and keep working later.
- **Formats.** Photos: JPG, JPEG, PNG, HEIC, HEIF, WEBP, AVIF, TIF, TIFF, BMP and camera RAW.
  Music: MP3, WAV, FLAC, M4A, AAC, WMA, OGG and Opus. Formats Windows can't play directly are
  converted automatically on your own PC.
  - If a free Microsoft extension needed for HEIC or RAW photos is missing, the app names it
    and links to it in the Microsoft Store.
- **Private by design.** No account, no telemetry, no network connections of any kind. Your
  photos and music are opened read-only and never changed.
- **Diagnostics.** Errors are recorded in a log that stays on your PC and is never sent
  anywhere. The About box can copy diagnostic info, open the log folder, or start a problem
  report by email.
