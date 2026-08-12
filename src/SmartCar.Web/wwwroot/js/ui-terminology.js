(() => {
    const replacementRules = [
        ["eKYC", "xác minh giấy tờ điện tử"],
        ["Hồ sơ KYC & độ tin cậy khách hàng", "Hồ sơ xác minh giấy tờ & độ tin cậy khách hàng"],
        ["KYC gồm 2 bước", "Xác minh giấy tờ gồm 2 bước"],
        ["Xem và đối chiếu KYC", "Xem và đối chiếu giấy tờ"],
        ["Đối chiếu hồ sơ KYC", "Đối chiếu hồ sơ giấy tờ"],
        ["Hồ sơ KYC đã được xác minh", "Hồ sơ giấy tờ đã được duyệt"],
        ["Hồ sơ KYC đã hoàn tất", "Xác minh giấy tờ đã hoàn tất"],
        ["Hồ sơ KYC cần cập nhật", "Hồ sơ giấy tờ cần cập nhật"],
        ["Hồ sơ KYC chờ duyệt", "Hồ sơ xác minh giấy tờ chờ duyệt"],
        ["Hồ sơ KYC chờ xử lý", "Hồ sơ xác minh giấy tờ chờ xử lý"],
        ["Hồ sơ KYC", "Hồ sơ xác minh giấy tờ"],
        ["KYC trong SmartCar", "Xác minh giấy tờ trong SmartCar"],
        ["quyết định KYC", "quyết định xác minh hồ sơ"],
        ["KYC chờ duyệt", "Hồ sơ xác minh giấy tờ chờ duyệt"],
        ["KYC chờ xử lý", "Hồ sơ xác minh giấy tờ chờ xử lý"],
        ["KYC", "xác minh giấy tờ"]
    ];

    const excludedTags = new Set(["SCRIPT", "STYLE", "CODE", "PRE", "NOSCRIPT"]);
    const translatedAttributes = ["title", "aria-label", "placeholder"];

    function translateText(value) {
        if (!value || !value.includes("KYC") && !value.includes("eKYC")) {
            return value;
        }

        let translated = value;
        for (const [from, to] of replacementRules) {
            translated = translated.split(from).join(to);
        }
        return translated;
    }

    function shouldSkipNode(node) {
        const parent = node.parentElement;
        return parent && excludedTags.has(parent.tagName);
    }

    function translateTextNode(node) {
        if (shouldSkipNode(node)) {
            return;
        }

        const translated = translateText(node.nodeValue ?? "");
        if (translated !== node.nodeValue) {
            node.nodeValue = translated;
        }
    }

    function translateElementAttributes(element) {
        for (const attributeName of translatedAttributes) {
            if (!element.hasAttribute(attributeName)) {
                continue;
            }

            const current = element.getAttribute(attributeName) ?? "";
            const translated = translateText(current);
            if (translated !== current) {
                element.setAttribute(attributeName, translated);
            }
        }
    }

    function translateTree(root) {
        if (!(root instanceof Node)) {
            return;
        }

        if (root.nodeType === Node.TEXT_NODE) {
            translateTextNode(root);
            return;
        }

        if (!(root instanceof Element) && root !== document.body) {
            return;
        }

        if (root instanceof Element) {
            translateElementAttributes(root);
            if (excludedTags.has(root.tagName)) {
                return;
            }
        }

        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let currentNode = walker.nextNode();
        while (currentNode) {
            translateTextNode(currentNode);
            currentNode = walker.nextNode();
        }

        if (root instanceof Element) {
            root.querySelectorAll("[title], [aria-label], [placeholder]")
                .forEach(translateElementAttributes);
        } else if (root === document.body) {
            document.querySelectorAll("[title], [aria-label], [placeholder]")
                .forEach(translateElementAttributes);
        }
    }

    function initializeTerminology() {
        if (!document.body) {
            return;
        }

        document.title = translateText(document.title);
        translateTree(document.body);

        const observer = new MutationObserver((mutations) => {
            for (const mutation of mutations) {
                if (mutation.type === "characterData") {
                    translateTextNode(mutation.target);
                    continue;
                }

                mutation.addedNodes.forEach(translateTree);
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true,
            characterData: true
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeTerminology, { once: true });
    } else {
        initializeTerminology();
    }
})();
