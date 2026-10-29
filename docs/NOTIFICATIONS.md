# Notifications on your phone

Levity can tell your phone when an answer or a background job has finished while the app is not open.

## Turn it on
1. Open Levity on your phone (on an iPhone: the installed app, iOS 16.4 or later).
2. Settings, **Notify me when something finishes**, **Turn on**, then allow notifications.

If you refuse the permission it can only be changed in the phone's own settings (iPhone: Settings, Levity; Android: long-press the icon, App info, Notifications).

## What it does and does not do
- The notice says only "Levity has news". It never contains your messages, file names or results; open the app to see them.
- It is sent only when no phone has the app open on screen, and at most once every 20 seconds.
- Nothing goes through the Levity relay. Your PC sends it straight to the phone vendor's push service (Google, Apple or Mozilla), signed with a key that is created on, and never leaves, your PC (`push.json` in the Levity data folder).
- It costs nothing: no account, key or plan is needed.
- Delivery is best effort. The phone vendors can delay or drop a push, so the app always shows the true state when you open it.
- Revoking a phone in Levity also removes its notification address.

## Limits
Three addresses per phone, ten in total. Only addresses on Google, Apple, Mozilla and Windows push services are accepted.
