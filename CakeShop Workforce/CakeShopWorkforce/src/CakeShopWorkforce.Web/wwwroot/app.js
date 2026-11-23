// Tiny helper for drag & drop on the Schedule page.
// Blazor handles the drop itself; Firefox only starts a drag when some data is
// attached to it, so we attach the chip's data-drag value here.
document.addEventListener("dragstart", (e) => {
    const chip = e.target instanceof Element ? e.target.closest("[data-drag]") : null;
    if (chip && e.dataTransfer) {
        e.dataTransfer.setData("text/plain", chip.getAttribute("data-drag"));
        e.dataTransfer.effectAllowed = "copy";
    }
});
