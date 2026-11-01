# OneMail.Render

Turns an email message into a tagged PDF or PNG pages in one call, on [OneRender](https://github.com/recursive-ai-oss/onerender-csharp). It is the optional rendering package of [OneMail](https://github.com/recursive-ai-oss/onemail-csharp): the message's display HTML (sanitised body, header block, attachment list, inline images from the message itself) is laid out and drawn without a browser.

**Early preview.** Native platforms only; not browser WebAssembly. The API is not stable yet.

```csharp
using RecursiveAI.OneMail;

IMailItem message = Mail.Parse(File.ReadAllBytes("message.eml"));
await using FileStream pdf = File.Create("message.pdf");
await message.ToPdfAsync(pdf);

await message.ToPngAsync(page => File.Create($"message-{page}.png"));
```

- Nothing is fetched. Remote images, fonts and style sheets stay blocked unless your policy allows them, and even then only a `Resources` callback you supply provides their bytes; what the page asked for and did not get is listed in the result.
- Text in scripts beyond Latin, Greek and Cyrillic (CJK, Arabic, Hebrew, emoji, ...) needs the matching `RecursiveAI.OneRender.Fonts.*` package referenced in your application; it registers itself.
- Limits are on by default: page count, time, and the sanitiser's size and nesting limits. A limit stops the render with a typed exception or is reported as a defect.

Project home and source: <https://github.com/recursive-ai-oss/onemail-csharp>. Details: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/render.md>. Licence: MIT OR Apache-2.0.

This project is unaffiliated with other products named OneMail.
