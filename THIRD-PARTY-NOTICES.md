# Third-party notices

Levity's own code is under the [PolyForm Noncommercial License 1.0.0](LICENSE). The components below are included or adapted under their own licenses, which are kept with them.

## GitDiagram (prompt wording)

The instructions Levity sends to the assistant when drawing an architecture diagram (`Services/DiagramPrompt.cs`) are adapted from the prompts in GitDiagram, along with its ideas of a structured graph, per-edge evidence and validation against the real project. No GitDiagram source code is included.

- Project: <https://github.com/ahmedkhaleel2004/gitdiagram>
- License: MIT

```
MIT License

Copyright (c) 2024 Ahmed Khaleel

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Mermaid (diagram drawing)

`wwwroot/vendor/mermaid.min.js` is Mermaid 11.10.1, unmodified, used to draw diagrams offline in the sidebar. The phone app uses the same version from npm. Its license text is in `wwwroot/vendor/mermaid.LICENSE`, and the file's own header lists the licenses of the libraries bundled inside it.

- Project: <https://github.com/mermaid-js/mermaid>
- License: MIT

## Other vendored files

| File | Project | License |
|---|---|---|
| `wwwroot/marked.min.js` | [marked](https://github.com/markedjs/marked) | MIT |
| `wwwroot/highlight.min.js`, `wwwroot/github-dark.min.css` | [highlight.js](https://github.com/highlightjs/highlight.js) | BSD-3-Clause |

The npm and NuGet dependencies listed in `web/package.json`, `relay/package.json` and `Levity.csproj` keep their own licenses.
