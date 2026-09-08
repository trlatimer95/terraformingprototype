# Docs

Source for the two documents that leave this project.

| File | What it is |
| --- | --- |
| `groundworks-playtest.html` | Tester-facing instructions for the demo build |
| `groundworks-terrain-architecture.html` | Terrain / strata / mining architecture proposal |

**The HTML is the source of truth.** The PDFs in the project root are generated from
it — edit the HTML, re-render, never edit a PDF. Both pages carry their own print
stylesheet, which forces a light palette so printing from a dark browser still puts ink on
white, and keeps each section whole across a page turn.

## Regenerating the PDFs

```powershell
.\Docs\render-pdfs.ps1
```

Output lands in the project root, next to the build, because that is where they get picked
up when a demo zip is assembled.

The renderer is headless Chrome rather than a Markdown-to-PDF tool: the pages use real
layout — SVG diagrams, tables, print-specific page-break rules — and the browser is the
only thing that agrees with what the artifact shows.
