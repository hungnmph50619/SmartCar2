(() => {
    const replacementRules = [
        ["eKYC", "xác minh danh tính điện tử"],
        ["Hồ sơ KYC & độ tin cậy khách hàng", "Hồ sơ xác minh danh tính & độ tin cậy khách hàng"],
        ["KYC gồm 2 bước", "Xác minh danh tính gồm 2 bước"],
        ["Xem và đối chiếu KYC", "Xem và đối chiếu thông tin xác minh"],
        ["Đối chiếu hồ sơ KYC", "Đối chiếu hồ sơ xác minh danh tính"],
        ["Hồ sơ KYC đã được xác minh", "Hồ sơ xác minh danh tính đã được duyệt"],
        ["Hồ sơ KYC đã hoàn tất", "Xác minh danh tính đã hoàn tất"],
        ["Hồ sơ KYC cần cập nhật", "Hồ sơ xác minh danh tính cần cập nhật"],
        ["Hồ sơ KYC chờ duyệt", "Hồ sơ xác minh danh tính chờ duyệt"],
        ["Hồ sơ KYC chờ xử lý", "Hồ sơ xác minh danh tính chờ xử lý"],
        ["Hồ sơ KYC", "Hồ sơ xác minh danh tính"],
        ["KYC trong SmartCar", "Xác minh danh tính trong SmartCar"],
        ["quyết định KYC", "quyết định xác minh danh tính"],
        ["KYC chờ duyệt", "Xác minh danh tính chờ duyệt"],
        ["KYC chờ xử lý", "Xác minh danh tính chờ xử lý"],
        ["KYC", "xác minh danh tính"]
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

    function syncAdminCustomerSidebar() {
        const pathname = window.location.pathname.toLowerCase();
        if (!pathname.startsWith("/admincustomers")) {
            return;
        }

        const params = new URLSearchParams(window.location.search);
        const isVerificationMode =
            params.get("profileStatus")?.toLowerCase() === "pending" ||
            params.get("tab")?.toLowerCase() === "documents";

        const links = Array.from(
            document.querySelectorAll(".admin-sidebar a.admin-nav-link"));

        const customerManagementLink = links.find((link) =>
            link.textContent?.includes("Quản lý khách hàng"));

        const verificationLink = links.find((link) =>
            link.textContent?.includes("Xác minh giấy tờ") ||
            link.textContent?.includes("Xác minh danh tính"));

        customerManagementLink?.classList.toggle("active", !isVerificationMode);
        verificationLink?.classList.toggle("active", isVerificationMode);
    }

    function clarifyAdminBookingRefundLabels() {
        const pathname = window.location.pathname.toLowerCase();
        if (!pathname.startsWith("/adminbookings/details/")) {
            return;
        }

        const rows = Array.from(document.querySelectorAll(".money-row"));
        if (rows.length === 0) {
            return;
        }

        const labelElement = (row) => row.querySelector(":scope > span");
        const labelText = (row) => {
            const element = labelElement(row);
            if (!element) return "";
            const firstTextNode = Array.from(element.childNodes)
                .find((node) => node.nodeType === Node.TEXT_NODE && node.nodeValue?.trim());
            return firstTextNode?.nodeValue?.trim() ?? "";
        };
        const setLabel = (row, value) => {
            const element = labelElement(row);
            if (!element) return;
            const firstTextNode = Array.from(element.childNodes)
                .find((node) => node.nodeType === Node.TEXT_NODE && node.nodeValue?.trim());
            if (firstTextNode) {
                firstTextNode.nodeValue = value;
            }
        };
        const amountValue = (row) => {
            const raw = row.querySelector(":scope > strong")?.textContent ?? "";
            const digits = raw.replace(/[^0-9]/g, "");
            return digits ? Number(digits) : 0;
        };

        const depositRows = rows.filter((row) => labelText(row) === "Hoàn cọc");
        const completed = document.body.textContent?.includes("Hồ sơ đã hoàn tất") === true;

        rows.forEach((row, index) => {
            if (labelText(row) !== "Hoàn chênh lệch đổi xe") {
                return;
            }

            const rentalDifference = amountValue(row);
            setLabel(row, "Hoàn chênh lệch tiền thuê do đổi xe");

            const previousRow = index > 0 ? rows[index - 1] : null;
            if (!previousRow || labelText(previousRow) !== "Hoàn cọc") {
                return;
            }

            const depositDifference = amountValue(previousRow);
            const matchesCurrentDepositPolicy =
                rentalDifference > 0 && depositDifference === Math.round(rentalDifference * Number(document.getElementById("booking-policy")?.dataset.depositRate || 3));
            const canDistinguishFromFinalDeposit = !completed || depositRows.length > 1;

            if (matchesCurrentDepositPolicy && canDistinguishFromFinalDeposit) {
                setLabel(previousRow, "Hoàn chênh lệch cọc do đổi xe");
            }
        });
    }

    function initializeIncrementalEvidenceImagePickers() {
        const supportedIds = new Set(["handover-images", "return-image-picker", "damage-images"]);
        const previousFilesByInput = new WeakMap();

        const isSupportedInput = (target) =>
            target instanceof HTMLInputElement &&
            target.type === "file" &&
            target.multiple &&
            supportedIds.has(target.id);

        const fileKey = (file) =>
            `${file.name}|${file.size}|${file.lastModified}|${file.type}`;

        document.addEventListener("click", (event) => {
            const input = event.target;
            if (!isSupportedInput(input)) {
                return;
            }

            previousFilesByInput.set(input, Array.from(input.files ?? []));
        }, true);

        document.addEventListener("change", (event) => {
            const input = event.target;
            if (!isSupportedInput(input)) {
                return;
            }

            const previousFiles = previousFilesByInput.get(input) ?? [];
            const newlyChosenFiles = Array.from(input.files ?? []);
            previousFilesByInput.delete(input);

            if (newlyChosenFiles.length === 0 || typeof DataTransfer === "undefined") {
                return;
            }

            const uniqueFiles = [];
            const seen = new Set();

            for (const file of [...previousFiles, ...newlyChosenFiles]) {
                const key = fileKey(file);
                if (seen.has(key)) {
                    continue;
                }

                seen.add(key);
                uniqueFiles.push(file);
            }

            const transfer = new DataTransfer();
            uniqueFiles.forEach((file) => transfer.items.add(file));
            input.files = transfer.files;
        }, true);
    }

    function initializeReturnEvidenceImageRemoval() {
        const pairs = [
            ["return-image-picker", "return-image-preview"],
            ["damage-images", "damage-image-preview"]
        ];

        for (const [inputId, previewId] of pairs) {
            const input = document.getElementById(inputId);
            const preview = document.getElementById(previewId);
            if (!(input instanceof HTMLInputElement) || !(preview instanceof HTMLElement)) {
                continue;
            }

            const addRemoveButtons = () => {
                Array.from(preview.children).forEach((wrapper, index) => {
                    if (!(wrapper instanceof HTMLElement)) {
                        return;
                    }

                    wrapper.classList.add("position-relative");
                    if (wrapper.querySelector("[data-remove-return-evidence]")) {
                        return;
                    }

                    const removeButton = document.createElement("button");
                    removeButton.type = "button";
                    removeButton.className = "btn btn-danger btn-sm rounded-circle position-absolute d-flex align-items-center justify-content-center";
                    removeButton.style.width = "24px";
                    removeButton.style.height = "24px";
                    removeButton.style.padding = "0";
                    removeButton.style.top = "-7px";
                    removeButton.style.right = "-7px";
                    removeButton.style.zIndex = "3";
                    removeButton.style.lineHeight = "1";
                    removeButton.innerHTML = "&times;";
                    removeButton.title = "Xóa ảnh này";
                    removeButton.setAttribute("aria-label", `Xóa ảnh ${index + 1}`);
                    removeButton.setAttribute("data-remove-return-evidence", "true");

                    removeButton.addEventListener("click", (event) => {
                        event.preventDefault();
                        event.stopPropagation();

                        const currentFiles = Array.from(input.files ?? []);
                        if (index < 0 || index >= currentFiles.length || typeof DataTransfer === "undefined") {
                            return;
                        }

                        const transfer = new DataTransfer();
                        currentFiles.forEach((file, fileIndex) => {
                            if (fileIndex !== index) {
                                transfer.items.add(file);
                            }
                        });

                        input.files = transfer.files;
                        input.dispatchEvent(new Event("change", { bubbles: true }));
                    });

                    wrapper.appendChild(removeButton);
                });
            };

            const observer = new MutationObserver(addRemoveButtons);
            observer.observe(preview, { childList: true });
            addRemoveButtons();
        }
    }

    function initializeReturnImageInlineValidation() {
        const imagePicker = document.getElementById("return-image-picker");
        if (!(imagePicker instanceof HTMLInputElement)) {
            return;
        }

        const damageImages = document.getElementById("damage-images");
        const hasDamage = document.getElementById("has-damage");
        const maximumImageBytes = 5 * 1024 * 1024;
        const minimumImages = 7;
        const maximumImages = 25;
        const allowedExtensions = [".jpg", ".jpeg", ".png", ".webp"];
        const allowedTypes = ["image/jpeg", "image/png", "image/webp"];

        let message = document.getElementById("return-image-inline-validation");
        if (!(message instanceof HTMLElement)) {
            message = document.createElement("div");
            message.id = "return-image-inline-validation";
            message.className = "text-danger small mt-1 d-none";
            imagePicker.insertAdjacentElement("afterend", message);
        }

        let touched = false;

        const fileError = (file) => {
            const lowerName = file.name.toLowerCase();
            const extensionValid = allowedExtensions.some((extension) => lowerName.endsWith(extension));
            const typeValid = !file.type || allowedTypes.includes(file.type.toLowerCase());

            if (!extensionValid || !typeValid) {
                return `Ảnh "${file.name}" không đúng định dạng. Chỉ chấp nhận JPG, PNG hoặc WEBP.`;
            }

            if (file.size > maximumImageBytes) {
                return `Ảnh "${file.name}" vượt quá 5 MB.`;
            }

            return "";
        };

        const render = () => {
            if (!touched) {
                return;
            }

            const files = Array.from(imagePicker.files ?? []);
            const damageFiles = hasDamage instanceof HTMLInputElement && hasDamage.checked &&
                damageImages instanceof HTMLInputElement
                ? Array.from(damageImages.files ?? [])
                : [];

            let validationMessage = "";
            if (files.length < minimumImages) {
                validationMessage = `Vui lòng chọn ít nhất ${minimumImages} ảnh trả xe.`;
            } else if (files.length + damageFiles.length > maximumImages) {
                validationMessage = `Tổng số ảnh trả xe và ảnh hư hỏng không được vượt quá ${maximumImages} ảnh.`;
            } else {
                validationMessage = [...files, ...damageFiles]
                    .map(fileError)
                    .find(Boolean) ?? "";
            }

            message.textContent = validationMessage;
            message.classList.toggle("d-none", !validationMessage);
        };

        const markTouchedAndRender = () => {
            touched = true;
            window.setTimeout(render, 0);
        };

        imagePicker.addEventListener("change", markTouchedAndRender);
        if (damageImages instanceof HTMLInputElement) {
            damageImages.addEventListener("change", markTouchedAndRender);
        }
        if (hasDamage instanceof HTMLInputElement) {
            hasDamage.addEventListener("change", markTouchedAndRender);
        }
        imagePicker.form?.addEventListener("submit", () => {
            touched = true;
            render();
        }, true);
    }

    function initializeTerminology() {
        if (!document.body) {
            return;
        }

        document.title = translateText(document.title);
        translateTree(document.body);

        // Chạy sau site.js để trạng thái menu Admin không bị script cũ ghi đè.
        syncAdminCustomerSidebar();
        clarifyAdminBookingRefundLabels();
        initializeIncrementalEvidenceImagePickers();
        initializeReturnEvidenceImageRemoval();
        initializeReturnImageInlineValidation();
        window.setTimeout(syncAdminCustomerSidebar, 0);
        window.setTimeout(clarifyAdminBookingRefundLabels, 0);

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
