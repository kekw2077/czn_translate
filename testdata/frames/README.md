# Frame set

PNG screenshots of the game, captured on the main machine and replayed here by
`FolderFrameSource` (TZ §12). Files are played in ordinal filename order, so a
`001-`, `002-` prefix keeps the sequence readable.

Next to each `frame.png` there may be a `frame.expected.json` describing what the
pipeline should produce for it:

```json
{
  "zones": {
    "dialogue": {
      "lines": [
        { "en": "Blood Pact", "ru": "Кровавый пакт", "source": "exact" }
      ]
    }
  }
}
```

The set is the regression suite for everything downstream of capture: change a
normalization rule or a search threshold and the diff against these files says
immediately what it moved.

Keep at least 30 frames, and make sure the hard ones are in there — mid-fade
text at partial opacity, the smallest font the UI uses, coloured `<color=…>`
runs, and a scrolling list. Those are the cases that break, and a set of thirty
clean dialogue boxes will happily pass while the real failures go unnoticed.

PNGs are gitignored; keep the set out of the repository and copy it in.
