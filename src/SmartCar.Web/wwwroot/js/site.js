document.addEventListener("DOMContentLoaded", () => {
    initializeVietnameseDateInputs();
    initializeSameAddressToggle();
    initializeForms();
    initializeImageInputs();
    initializeKycEditToggles();
    initializeAdminCustomerNavigation();
});

function initializeForms() {
    document.querySelectorAll("form[data-confirm], form[data-loading-form]").forEach((form) => {
        form.addEventListener("submit", (event) => {
            if (event.defaultPrevented || !form.checkValidity()) {
                return;
            }

            if (!passesJQueryValidation(form)) {
                event.preventDefault();
                return;
            }

            const confirmationMessage = form.dataset.confirm;
            if (confirmationMessage && !window.confirm(confirmationMessage)) {
                event.preventDefault();
                return;
            }

            if (!form.hasAttribute("data-loading-form")) {
                return;
            }

            const submitter = event.submitter ?? form.querySelector('button[type="submit"], input[type="submit"]');
            if (!submitter || submitter.disabled) {
                return;
            }

            submitter.disabled = true;
            submitter.setAttribute("aria-busy", "true");

            if (submitter instanceof HTMLButtonElement) {
                const loadingText = submitter.dataset.loadingText ?? "Đang xử lý...";
                submitter.dataset.originalHtml = submitter.innerHTML;
                submitter.innerHTML = `
                    <span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>
                    <span>${escapeHtml(loadingText)}</span>`;
            } else {
                submitter.dataset.originalValue = submitter.value;
                submitter.value = submitter.dataset.loadingText ?? "Đang xử lý...";
            }
        });
    });
}

function passesJQueryValidation(form) {
    const jq = window.jQuery;
    if (!jq || !jq.validator) {
        return true;
    }

    const wrapped = jq(form);
    if (typeof wrapped.valid !== "function") {
        return true;
    }

    return wrapped.valid();
}

function initializeVietnameseDateInputs() {
    document.querySelectorAll("input[data-vn-date-input]").forEach((displayInput) => {
        if (!(displayInput instanceof HTMLInputElement)) {
            return;
        }

        const hiddenSelector = displayInput.dataset.hiddenTarget;
        const hiddenInput = hiddenSelector ? document.querySelector(hiddenSelector) : null;
        if (!(hiddenInput instanceof HTMLInputElement)) {
            return;
        }

        const syncToHidden = (showError) => {
            const value = displayInput.value.trim();
            if (!value) {
                hiddenInput.value = "";
                displayInput.setCustomValidity("");
                return true;
            }

            const parsed = parseVietnameseDate(value);
            if (!parsed) {
                hiddenInput.value = "";
                displayInput.setCustomValidity(showError
                    ? "Vui lòng nhập ngày hợp lệ, ví dụ 28/7/2026 hoặc 28/07/2026."
                    : "");
                return false;
            }

            const day = parsed.day.toString().padStart(2, "0");
            const month = parsed.month.toString().padStart(2, "0");
            const year = parsed.year.toString().padStart(4, "0");
            hiddenInput.value = `${year}-${month}-${day}`;

            const businessError = getDateBusinessError(displayInput, parsed);
            displayInput.setCustomValidity(showError && businessError ? businessError : "");
            return !businessError;
        };

        const normalizeDisplay = () => {
            const parsed = parseVietnameseDate(displayInput.value);
            if (!parsed) {
                return;
            }

            displayInput.value = formatVietnameseDate(parsed);
        };

        if (!displayInput.value && hiddenInput.value) {
            const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(hiddenInput.value);
            if (match) {
                displayInput.value = `${match[3]}/${match[2]}/${match[1]}`;
            }
        }

        displayInput.addEventListener("input", () => syncToHidden(false));
        displayInput.addEventListener("blur", () => {
            const valid = syncToHidden(true);
            if (valid) {
                normalizeDisplay();
            } else if (!displayInput.checkValidity()) {
                displayInput.reportValidity();
            }
        });

        displayInput.form?.addEventListener("submit", (event) => {
            if (!syncToHidden(true)) {
                event.preventDefault();
                displayInput.reportValidity();
                return;
            }
            normalizeDisplay();
        });
    });
}

function parseVietnameseDate(value) {
    const trimmed = value.trim();
    let match = /^(\d{1,2})[\/\.\-](\d{1,2})[\/\.\-](\d{4})$/.exec(trimmed);

    if (!match && /^\d{8}$/.test(trimmed)) {
        match = /^(\d{2})(\d{2})(\d{4})$/.exec(trimmed);
    }

    if (!match) {
        return null;
    }

    const day = Number(match[1]);
    const month = Number(match[2]);
    const year = Number(match[3]);
    const date = new Date(year, month - 1, day);
    const isValid = date.getFullYear() === year &&
        date.getMonth() === month - 1 &&
        date.getDate() === day;

    return isValid ? { day, month, year } : null;
}

function parseIsoDate(value) {
    if (!value) {
        return null;
    }

    const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value.trim());
    if (!match) {
        return null;
    }

    return {
        year: Number(match[1]),
        month: Number(match[2]),
        day: Number(match[3])
    };
}

function getDateBusinessError(input, parsed) {
    const role = input.dataset.dateRole;
    const documentLabel = input.dataset.documentLabel ?? "Giấy tờ";
    const today = getTodayParts();

    if (role === "birth") {
        const latestAllowedBirthDate = {
            day: today.day,
            month: today.month,
            year: today.year - 18
        };

        if (compareDateParts(parsed, latestAllowedBirthDate) > 0) {
            return "Khách thuê xe phải đủ 18 tuổi.";
        }
    }

    if (role === "issued" && compareDateParts(parsed, today) > 0) {
        return "Ngày cấp không được sau ngày hiện tại.";
    }

    if (role === "expiry") {
        if (compareDateParts(parsed, today) < 0) {
            return `${documentLabel} đã hết hạn.`;
        }

        const issuedSelector = input.dataset.relatedIssued;
        const issuedInput = issuedSelector ? document.querySelector(issuedSelector) : null;
        if (issuedInput instanceof HTMLInputElement) {
            const issuedDate = parseVietnameseDate(issuedInput.value);
            if (issuedDate && compareDateParts(parsed, issuedDate) <= 0) {
                return "Ngày hết hạn phải sau ngày cấp.";
            }
        }

        const requiredThrough = parseIsoDate(input.dataset.minValidThrough ?? "");
        if (requiredThrough && compareDateParts(parsed, requiredThrough) < 0) {
            return `${documentLabel} phải còn hiệu lực ít nhất đến ngày ${formatVietnameseDate(requiredThrough)}.`;
        }
    }

    return null;
}

function getTodayParts() {
    const now = new Date();
    return {
        day: now.getDate(),
        month: now.getMonth() + 1,
        year: now.getFullYear()
    };
}

function compareDateParts(left, right) {
    const leftValue = left.year * 10000 + left.month * 100 + left.day;
    const rightValue = right.year * 10000 + right.month * 100 + right.day;
    return Math.sign(leftValue - rightValue);
}

function formatVietnameseDate(value) {
    return `${value.day.toString().padStart(2, "0")}/${value.month.toString().padStart(2, "0")}/${value.year}`;
}

function initializeSameAddressToggle() {
    document.querySelectorAll("input[data-copy-address-from][data-copy-address-to]").forEach((toggle) => {
        if (!(toggle instanceof HTMLInputElement)) {
            return;
        }

        const source = document.querySelector(toggle.dataset.copyAddressFrom);
        const target = document.querySelector(toggle.dataset.copyAddressTo);
        if (!(source instanceof HTMLInputElement) || !(target instanceof HTMLInputElement)) {
            return;
        }

        const copyAddress = () => {
            if (toggle.checked) {
                target.value = source.value;
                target.readOnly = true;
                target.dispatchEvent(new Event("input", { bubbles: true }));
            } else {
                target.readOnly = false;
            }
        };

        toggle.addEventListener("change", copyAddress);
        source.addEventListener("input", () => {
            if (toggle.checked) {
                target.value = source.value;
                target.dispatchEvent(new Event("input", { bubbles: true }));
            }
        });
        copyAddress();
    });
}

function initializeImageInputs() {
    document.querySelectorAll("input[type='file'][data-image-input]").forEach((input) => {
        if (!(input instanceof HTMLInputElement)) {
            return;
        }

        const previewSelector = input.dataset.previewTarget;
        const infoSelector = input.dataset.fileInfoTarget;
        const previewContainer = previewSelector ? document.querySelector(previewSelector) : null;
        const infoContainer = infoSelector ? document.querySelector(infoSelector) : null;
        const previewImage = previewContainer?.querySelector("img") ?? null;
        const previewList = previewContainer?.querySelector("[data-preview-list]") ?? null;
        const clearButton = previewContainer?.querySelector("[data-clear-image]") ?? null;
        const maximumBytes = Number(input.dataset.maxBytes ?? 5 * 1024 * 1024);
        const allowedExtensions = [".jpg", ".jpeg", ".png", ".webp"];
        const allowedMimeTypes = ["image/jpeg", "image/png", "image/webp"];
        let objectUrls = [];

        const releaseObjectUrls = () => {
            objectUrls.forEach((url) => URL.revokeObjectURL(url));
            objectUrls = [];
        };

        const resetInfoStyle = () => {
            infoContainer?.classList.remove("text-danger");
            infoContainer?.classList.add("text-muted");
        };

        const showFileError = (message) => {
            input.setCustomValidity(message);
            releaseObjectUrls();
            previewContainer?.classList.add("d-none");
            if (infoContainer) {
                infoContainer.textContent = message;
                infoContainer.classList.remove("d-none", "text-muted");
                infoContainer.classList.add("text-danger");
            }
            input.reportValidity();
        };

        const clearPreview = () => {
            releaseObjectUrls();
            input.value = "";
            input.setCustomValidity("");
            previewContainer?.classList.add("d-none");

            if (previewImage) {
                previewImage.removeAttribute("src");
            }

            if (previewList) {
                previewList.innerHTML = "";
            }

            if (infoContainer) {
                infoContainer.textContent = "Chưa chọn ảnh";
                infoContainer.classList.remove("d-none");
                resetInfoStyle();
            }
        };

        input.addEventListener("change", () => {
            const files = Array.from(input.files ?? []);
            if (files.length === 0) {
                clearPreview();
                return;
            }

            const invalidFormat = files.find((file) => {
                const lowerName = file.name.toLowerCase();
                const hasAllowedExtension = allowedExtensions.some((extension) => lowerName.endsWith(extension));
                const hasAllowedMimeType = !file.type || allowedMimeTypes.includes(file.type.toLowerCase());
                return !hasAllowedExtension || !hasAllowedMimeType;
            });
            if (invalidFormat) {
                showFileError("Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.");
                return;
            }

            const oversizedFile = files.find((file) => file.size > maximumBytes);
            if (oversizedFile) {
                showFileError(`Ảnh ${oversizedFile.name} vượt quá ${formatBytes(maximumBytes)}.`);
                return;
            }

            input.setCustomValidity("");
            releaseObjectUrls();
            objectUrls = files.map((file) => URL.createObjectURL(file));

            if (previewList) {
                previewList.innerHTML = "";
                files.forEach((file, index) => {
                    const column = document.createElement("div");
                    column.className = "col-6 col-md-4";

                    const image = document.createElement("img");
                    image.src = objectUrls[index];
                    image.alt = `Xem trước ${file.name}`;
                    image.className = "img-fluid rounded border w-100";
                    image.style.height = "140px";
                    image.style.objectFit = "cover";

                    const caption = document.createElement("div");
                    caption.className = "small text-truncate mt-1";
                    caption.title = file.name;
                    caption.textContent = file.name;

                    column.append(image, caption);
                    previewList.appendChild(column);
                });
            } else if (previewImage) {
                previewImage.src = objectUrls[0];
            }

            if (infoContainer) {
                const totalSize = files.reduce((sum, file) => sum + file.size, 0);
                infoContainer.textContent = files.length === 1
                    ? `${files[0].name} · ${formatBytes(files[0].size)}`
                    : `${files.length} ảnh · Tổng dung lượng ${formatBytes(totalSize)}`;
                infoContainer.classList.remove("d-none");
                resetInfoStyle();
            }

            previewContainer?.classList.remove("d-none");
        });

        clearButton?.addEventListener("click", clearPreview);
        window.addEventListener("beforeunload", releaseObjectUrls, { once: true });
    });
}

function initializeKycEditToggles() {
    document.querySelectorAll("[data-edit-toggle]").forEach((toggle) => {
        if (!(toggle instanceof HTMLElement)) {
            return;
        }

        const targetSelector = toggle.dataset.editToggle;
        const panel = targetSelector ? document.querySelector(targetSelector) : null;
        if (!(panel instanceof HTMLElement)) {
            return;
        }

        toggle.addEventListener("click", () => {
            const willOpen = panel.classList.contains("d-none");
            panel.classList.toggle("d-none", !willOpen);
            toggle.setAttribute("aria-expanded", willOpen ? "true" : "false");

            if (willOpen) {
                const firstControl = panel.querySelector("input:not([type='hidden']), select, textarea");
                if (firstControl instanceof HTMLElement) {
                    firstControl.focus();
                }
            }
        });
    });
}

function initializeAdminCustomerNavigation() {
    const isCustomerAdminPage = window.location.pathname.toLowerCase().startsWith("/admincustomers");

    if (isCustomerAdminPage && !document.querySelector('link[href*="admin-customers.css"]')) {
        const stylesheet = document.createElement("link");
        stylesheet.rel = "stylesheet";
        stylesheet.href = "/css/admin-customers.css";
        document.head.appendChild(stylesheet);
    }

    const customerLink = Array.from(document.querySelectorAll(".admin-sidebar a.admin-nav-link"))
        .find((link) => link.getAttribute("href")?.includes("/AdminDocuments"));

    if (!customerLink) {
        return;
    }

    customerLink.setAttribute("href", "/AdminCustomers");
    const label = customerLink.querySelector("span:last-child");
    if (label) {
        label.textContent = "Quản lý khách hàng";
    }

    if (isCustomerAdminPage) {
        customerLink.classList.add("active");
    }
}

function formatBytes(bytes) {
    if (bytes < 1024) {
        return `${bytes} B`;
    }

    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }

    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}

function escapeHtml(value) {
    const element = document.createElement("div");
    element.textContent = value;
    return element.innerHTML;
}
