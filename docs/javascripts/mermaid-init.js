document.addEventListener("DOMContentLoaded", function () {
  // fence_code_format wraps content in <pre class="mermaid"><code>...</code></pre>
  // Mermaid needs the text directly in the element it renders.
  // Convert each <pre class="mermaid"><code> block into a <div class="mermaid">.
  document.querySelectorAll("pre.mermaid > code").forEach(function (code) {
    var div = document.createElement("div");
    div.className = "mermaid";
    div.textContent = code.textContent;
    code.parentElement.replaceWith(div);
  });

  mermaid.initialize({ startOnLoad: true, theme: "default" });
});
