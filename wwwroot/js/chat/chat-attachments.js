// ---------------------------------------------------------------------------
//  ChatApp - chat-attachments.js
//
//  Rendering a photo or a file card inside a bubble.
//
//  An ES module. Nothing here is global: what this file needs, it imports, and
//  what other files need from it, it exports. There is no load order to keep in
//  your head - chat-boot.js is the single entry and the module graph decides
//  the rest.
//
//  Two rules this file lives under:
//
//  chat-core.js imports NOTHING. It owns `connection`, and a cycle back into it
//  lets another module reach that const while it is still in its dead zone -
//  which is exactly how this refactor failed the first time.
//
//  Shared mutable state lives in chat-state.js, because an imported binding is
//  read-only at the importing end. If two files write the same value, it
//  belongs on that object, not in a `let` here.
// ---------------------------------------------------------------------------

import { pinWhileMediaLoads } from './chat-core.js';
import { toggleMeta } from './chat-days.js';
import { writeMessageText } from './chat-text.js';

    // ======================================================================
    //  ATTACHMENTS
    //  A photo or file is uploaded as soon as it is picked, which gives us an
    //  id; the id then rides along with the next message the user sends. That
    //  keeps the hub call small and means the upload has usually finished
    //  before they have finished typing.
    // ======================================================================
    export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;


    export function formatBytes(bytes) {
        if (!bytes && bytes !== 0) return "";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(0) + " KB";
        return (bytes / (1024 * 1024)).toFixed(1) + " MB";
    }

    // Builds the node shown inside a message bubble for an attachment.
    export function buildAttachmentNode(att) {
        if (!att || !att.id) return null;

        const url = `/api/attachments/${att.id}`;

        if (att.isImage) {
            const link = document.createElement("a");
            link.href = url;
            link.target = "_blank";
            link.rel = "noopener";
            link.className = "attachment-image";

            const img = document.createElement("img");
            img.src = url;
            img.alt = att.fileName || "photo";
            // deliberately NOT loading="lazy" - see pinWhileMediaLoads()
            img.className = "loading";
            img.addEventListener("load", () => img.classList.remove("loading"), { once: true });
            img.addEventListener("error", () => img.classList.remove("loading"), { once: true });
            link.appendChild(img);
            return link;
        }

        const link = document.createElement("a");
        link.href = url;
        link.className = "attachment-file";
        link.setAttribute("download", att.fileName || "file");

        const icon = document.createElement("span");
        icon.className = "attachment-file-icon";
        icon.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">' +
            '<path d="M13 3H7a1.6 1.6 0 0 0-1.6 1.6v14.8A1.6 1.6 0 0 0 7 21h10a1.6 1.6 0 0 0 1.6-1.6V8.6z" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/>' +
            '<path d="M13 3v5.6h5.6" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg>';

        const meta = document.createElement("span");
        meta.className = "attachment-file-meta";

        const name = document.createElement("span");
        name.className = "attachment-file-name";
        name.textContent = att.fileName || "file";      // textContent: the name came from a user

        const size = document.createElement("span");
        size.className = "attachment-file-size";
        size.textContent = formatBytes(att.size);

        meta.appendChild(name);
        meta.appendChild(size);
        link.appendChild(icon);
        link.appendChild(meta);
        return link;
    }

    // Fills a message bubble with its attachment (if any) and its text (if any).
    // Every render path goes through here so the four of them cannot drift.
    export function fillBubble(container, text, attachment, opts) {
        // The timestamp rides on the bubble as a data attribute, NOT as a child
        // element - anything inside the bubble inherits the bubble's own
        // background. The line is built outside it on demand, see toggleMeta().
        const when = opts && opts.time;
        if (when) container.dataset.sentAt = when;
        if (opts && opts.id) container.dataset.messageId = opts.id;
        const node = buildAttachmentNode(attachment);
        if (node) {
            container.classList.add("has-attachment");
            // a photo fills the bubble edge to edge, a file card does not
            if (attachment.isImage) container.classList.add("has-image");
            container.appendChild(node);
        }
        if (text) {
            const el = document.createElement((opts && opts.tag) || "span");
            el.className = "message-text" + ((opts && opts.cls) ? " " + opts.cls : "");
            writeMessageText(el, text);
            container.appendChild(el);
        }
    }

