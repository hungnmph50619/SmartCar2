document.addEventListener("DOMContentLoaded", () => {
    initializeForms();
    initializeImageInputs();
    initializeAdminCustomerNavigation();
    initializeDocumentUpdateButtons();
});

function initializeForms() {
    document.querySelectorAll("form[data-confirm], form[data-loading-form]").forEach((form) => {
        form.addEventListener("submit", (event) => {
            if (event.defaultPrevented || !form.checkValidity()) {
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

function initializeImageInputs() {
    document.querySelectorAll("input[type='file'][data-image-input]").forEach((input) => {
        const previewSelector = input.dataset.previewTarget;
        const infoSelector = input.dataset.fileInfoTarget;
        const previewContainer = previewSelector ? document.querySelector(previewSelector) : null;
        const infoContainer = infoSelector ? document.querySelector(infoSelector) : null;
        const previewImage = previewContainer?.querySelector("img") ?? null;
        const previewList = previewContainer?.querySelector("[data-preview-list]") ?? null;
        const clearButton = previewContainer?.querySelector("[data-clear-image]") ?? null;
        let objectUrls = [];

        const releaseObjectUrls = () => {
            objectUrls.forEach((url) => URL.revokeObjectURL(url));
            objectUrls = [];
        };

        const clearPreview = () => {
            releaseObjectUrls();
            input.value = "";
            previewContainer?.classList.add("d-none");
            infoContainer?.classList.add("d-none");

            if (previewImage) {
                previewImage.removeAttribute("src");
            }

            if (previewList) {
                previewList.innerHTML = "";
            }

            if (infoContainer) {
                infoContainer.textContent = "";
            }
        };

        input.addEventListener("change", () => {
            const files = Array.from(input.files ?? []);
            if (files.length === 0) {
                clearPreview();
                return;
            }

            if (files.some((file) => !file.type.startsWith("image/"))) {
                clearPreview();
                return;
            }

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
            }

            previewContainer?.classList.remove("d-none");
        });

        clearButton?.addEventListener("click", clearPreview);
        window.addEventListener("beforeunload", releaseObjectUrls, { once: true });
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

function initializeDocumentUpdateButtons() {
    const form = document.getElementById("profile-document-upload-form");
    const typeSelect = document.getElementById("profile-document-type");

    if (!form || !(typeSelect instanceof HTMLSelectElement)) {
        return;
    }

    const citizenIdFrontType = "CCCD";
    const citizenIdBackType = "CCCD mặt sau";
    const drivingLicenseType = "GPLX";
    const citizenIdNumber = form.dataset.citizenIdNumber ?? "";
    const maskedCitizenIdNumber = form.dataset.citizenIdMaskedNumber ?? "Chưa có";

    const numberInput = form.querySelector('input[name="DocumentUpload.DocumentNumber"]');
    const numberGroup = document.getElementById("profile-document-number-group");
    const numberSummary = document.getElementById("profile-document-number-summary");
    const citizenIdSummary = numberSummary?.querySelector("[data-citizen-id-summary]") ?? null;
    const expiryGroup = document.getElementById("profile-document-expiry-group");
    const expiryDisplay = document.getElementById("profile-document-expiry-display");
    const expiryValue = document.getElementById("profile-document-expiry-value");
    const imageInput = form.querySelector('input[name="DocumentUpload.Image"]');
    const submitButton = document.getElementById("profile-document-submit");
    let previousType = typeSelect.value;

    const setExpiryFromDisplay = () => {
        if (!(expiryDisplay instanceof HTMLInputElement) || !(expiryValue instanceof HTMLInputElement)) {
            return;
        }

        const value = expiryDisplay.value.trim();
        if (!value) {
            expiryValue.value = "";
            expiryDisplay.setCustomValidity("");
            return;
        }

        const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value);
        if (!match) {
            expiryValue.value = "";
            expiryDisplay.setCustomValidity("Vui lòng nhập ngày theo định dạng dd/mm/yyyy.");
            return;
        }

        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);
        const isValid = date.getFullYear() === year &&
            date.getMonth() === month - 1 &&
            date.getDate() === day;

        if (!isValid) {
            expiryValue.value = "";
            expiryDisplay.setCustomValidity("Ngày hết hạn không hợp lệ.");
            return;
        }

        expiryDisplay.setCustomValidity("");
        expiryValue.value = `${year.toString().padStart(4, "0")}-${month.toString().padStart(2, "0")}-${day.toString().padStart(2, "0")}`;
    };

    const restoreExpiryDisplay = () => {
        if (!(expiryDisplay instanceof HTMLInputElement) || !(expiryValue instanceof HTMLInputElement)) {
            return;
        }

        const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(expiryValue.value);
        if (match) {
            expiryDisplay.value = `${match[3]}/${match[2]}/${match[1]}`;
        }
    };

    const syncDocumentFields = () => {
        const type = typeSelect.value;
        const isCitizenIdBack = type === citizenIdBackType;
        const isDrivingLicense = type === drivingLicenseType;

        numberGroup?.classList.toggle("d-none", isCitizenIdBack);
        numberSummary?.classList.toggle("d-none", !isCitizenIdBack);
        expiryGroup?.classList.toggle("d-none", !isDrivingLicense);

        if (isCitizenIdBack && numberInput instanceof HTMLInputElement) {
            numberInput.value = citizenIdNumber;
        }

        if (citizenIdSummary) {
            citizenIdSummary.textContent = citizenIdNumber
                ? maskedCitizenIdNumber
                : "Chưa có CCCD mặt trước";
        }

        if (expiryDisplay instanceof HTMLInputElement) {
            expiryDisplay.required = isDrivingLicense;
            if (!isDrivingLicense) {
                expiryDisplay.value = "";
                expiryDisplay.setCustomValidity("");
            }
        }

        if (expiryValue instanceof HTMLInputElement && !isDrivingLicense) {
            expiryValue.value = "";
        }

        if (submitButton instanceof HTMLButtonElement) {
            submitButton.disabled = isCitizenIdBack && !citizenIdNumber;
        }
    };

    restoreExpiryDisplay();
    syncDocumentFields();

    typeSelect.addEventListener("change", () => {
        const nextType = typeSelect.value;
        if (nextType !== previousType &&
            nextType !== citizenIdBackType &&
            numberInput instanceof HTMLInputElement) {
            numberInput.value = "";
        }

        previousType = nextType;
        syncDocumentFields();
    });

    if (expiryDisplay instanceof HTMLInputElement) {
        expiryDisplay.addEventListener("input", setExpiryFromDisplay);
        expiryDisplay.addEventListener("blur", () => {
            setExpiryFromDisplay();
            if (!expiryDisplay.checkValidity()) {
                expiryDisplay.reportValidity();
            }
        });
    }

    form.addEventListener("submit", () => {
        if (typeSelect.value === drivingLicenseType) {
            setExpiryFromDisplay();
        }
    });

    document.querySelectorAll("[data-document-update-button]").forEach((button) => {
        button.addEventListener("click", () => {
            const documentType = button.dataset.documentType;
            if (documentType) {
                typeSelect.value = documentType;
                typeSelect.dispatchEvent(new Event("change", { bubbles: true }));
            }

            form.scrollIntoView({ behavior: "smooth", block: "start" });
            window.setTimeout(() => {
                if (typeSelect.value === citizenIdBackType && imageInput instanceof HTMLElement) {
                    imageInput.focus({ preventScroll: true });
                } else if (numberInput instanceof HTMLElement) {
                    numberInput.focus({ preventScroll: true });
                }
            }, 450);
        });
    });
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
