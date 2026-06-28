window.imageVault = window.imageVault || {};

window.imageVault.viewerKeys = {
    connect(dotNetReference) {
        this.disconnect();
        this.reference = dotNetReference;
        this.handler = event => {
            if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || isTextInput(event.target)) {
                return;
            }

            if (event.key === "ArrowLeft") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("PreviousImageFromKeyboard");
            } else if (event.key === "ArrowRight") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("NextImageFromKeyboard");
            } else if (event.key === "Escape") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("CloseViewerFromKeyboard");
            }
        };

        window.addEventListener("keydown", this.handler);
    },

    disconnect() {
        if (this.handler) {
            window.removeEventListener("keydown", this.handler);
            this.handler = null;
        }

        this.reference = null;
    }
};

window.imageVault.labelAffeKeys = {
    connect(dotNetReference) {
        this.disconnect();
        this.reference = dotNetReference;
        this.handler = event => {
            const inTextInput = isTextInput(event.target);
            const isSaveShortcut = (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "s";
            const isSaveNextShortcut = (event.ctrlKey || event.metaKey) && !event.altKey && event.key === "Enter";
            const isModifiedPrevious = event.altKey && !event.ctrlKey && !event.metaKey && (event.key === "ArrowLeft" || event.key === "ArrowUp");
            const isModifiedNext = event.altKey && !event.ctrlKey && !event.metaKey && (event.key === "ArrowRight" || event.key === "ArrowDown");

            if (isSaveShortcut) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("SaveLabelArchiveFromKeyboard");
                return;
            }

            if (isSaveNextShortcut) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("SaveAndNextLabelArchiveFromKeyboard");
                return;
            }

            if (isModifiedPrevious) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("PreviousLabelArchiveFromKeyboard");
                return;
            }

            if (isModifiedNext) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("NextLabelArchiveFromKeyboard");
                return;
            }

            if (!event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && inTextInput) {
                if (event.key === "ArrowUp") {
                    event.preventDefault();
                    dotNetReference.invokeMethodAsync("PreviousLabelArchiveFromKeyboard");
                } else if (event.key === "ArrowDown") {
                    event.preventDefault();
                    dotNetReference.invokeMethodAsync("NextLabelArchiveFromKeyboard");
                }

                return;
            }

            if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
                return;
            }

            if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("PreviousLabelArchiveFromKeyboard");
            } else if (event.key === "ArrowRight" || event.key === "ArrowDown") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("NextLabelArchiveFromKeyboard");
            } else if (event.key === "Escape") {
                event.preventDefault();
                dotNetReference.invokeMethodAsync("CloseLabelAffeFromKeyboard");
            }
        };

        window.addEventListener("keydown", this.handler);
    },

    disconnect() {
        if (this.handler) {
            window.removeEventListener("keydown", this.handler);
            this.handler = null;
        }

        this.reference = null;
    }
};

window.imageVault.scrollActiveFilmstripItem = () => {
    requestAnimationFrame(() => {
        document.querySelector(".viewer .filmstrip-item.active")?.scrollIntoView({
            behavior: "smooth",
            block: "nearest",
            inline: "center"
        });
    });
};

window.imageVault.scrollActiveLabelArchive = () => {
    requestAnimationFrame(() => {
        document.querySelector(".labelaffe-archive.active")?.scrollIntoView({
            behavior: "smooth",
            block: "nearest",
            inline: "nearest"
        });
    });
};

function isTextInput(target) {
    const tagName = target?.tagName?.toLowerCase();
    return tagName === "input" || tagName === "textarea" || tagName === "select" || target?.isContentEditable;
}
