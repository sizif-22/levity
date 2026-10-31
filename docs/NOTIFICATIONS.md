# Notifications on your phone

Levity can tell your phone when an answer or a background job has finished while the app is not open.

## Turn it on
1. Open Levity on your phone (on an iPhone: the installed app, iOS 16.4 or later).
2. Settings, **Notify me when something finishes**, **Turn on**, then allow notifications.

If you refuse the permission it can only be changed in the phone's own settings (iPhone: Settings, Levity; Android: long-press the icon, App info, Notifications).

## What a notice says
When an answer finishes, the notice looks like this:

```
Levity · PostPilot · done
Re: I need you to think of a way to publish images as posts on TikTok…
I have created a comprehensive implementation plan to enable image post publishing on TikTok.
44 steps · 11 min 02 s · 3 files changed (+120/-8)
```

- **Title:** the project folder, and "done" or "failed".
- **Re:** what you asked (first 70 characters).
- **Answer:** how the assistant's *last* message starts (150 characters, markdown and links removed). The opening "let me look at..." is not used.
- **Last line:** steps taken, time, and files changed since the checkpoint (needs a git folder).
- A finished background job names the project, its status and the command: "Levity · PostPilot · job failed (exit 1)" and "Job #3: npx tsc --noEmit".
- Notices from different projects stay separate on the phone; a newer notice replaces an older one of the same project only.

## What it does and does not do
- **The text is encrypted to your phone** (RFC 8291, `aes128gcm`). Apple, Google and Mozilla only relay bytes they cannot read, and nothing goes through the Levity relay. Once it arrives, your **lock screen shows the text**, so anyone who can see your lock screen can read it. To keep notices plain ("Levity has news / Open Levity to see it."), set `LEVITY_PUSH_DETAILS=0` in `.env` and restart Levity.
- It is sent only when no phone has the app open on screen, and at most once every 20 seconds.
- Your PC sends it straight to the phone vendor's push service, signed with a key that is created on, and never leaves, your PC (`push.json` in the Levity data folder).
- It costs nothing: no account, key or plan is needed.
- Delivery is best effort. The phone vendors can delay or drop a push, so the app always shows the true state when you open it.
- Revoking a phone in Levity also removes its notification address.

## Still seeing "Levity has news"?
That means the PC could not encrypt a message for the phone. Check, in order:
1. **The PC app is older than this feature.** Rebuild and restart Levity.
2. **The phone subscribed before notices carried text** (its subscription has no keys). Open the Levity app on the phone once, with the new version deployed: it renews the subscription by itself, without a prompt. Or turn notifications off and on in Settings.
3. **The phone app is an old deploy.** Rebuild and deploy `web` (`npm run build`); an installed iPhone app picks up the new version the next time it opens online.
4. **`LEVITY_PUSH_DETAILS=0`** is set.

## Limits
Three addresses per phone, ten in total. Only addresses on Google, Apple, Mozilla and Windows push services are accepted.
