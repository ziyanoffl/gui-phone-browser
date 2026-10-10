# Project guidelines

- Keep the app simple: one Windows desktop app and one project unless a real need arises.
- Prefer a polished, clear UI and small, readable changes over extra features or abstractions.
- Publish a portable self-contained Windows `.exe`; keep required ADB files embedded and explain any runtime extraction.
- Android phone transfers should use ADB. Preserve timestamps wherever the source and destination support them, and explain any limitations in the UI or handover notes.
- Be careful with move operations: verify file size and available modified time before deleting the source. Never treat a folder as verified based only on its destination directory existing.
- Preserve modified time where possible and state clearly that Android creation time is not portable through this ADB flow.
- Keep this file and `HANDOVER.md` current when the project direction or known limitations change.
- Do not add dependencies, projects, or broad features without a clear need.
- Keep handover claims aligned with implemented behavior; a smoke check is not a real-device transfer test.
