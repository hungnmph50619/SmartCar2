(() => {
    const form = document.getElementById('vehicleDocumentForm');
    const input = document.getElementById('ImageFile');
    const prompt = document.getElementById('docUploadPrompt');
    const preview = document.getElementById('docPreview');
    const image = document.getElementById('docPreviewImage');
    const name = document.getElementById('docPreviewName');
    const size = document.getElementById('docPreviewSize');
    const remove = document.getElementById('docRemoveFile');
    const documentType = document.getElementById('DocumentType');
    const documentNumber = document.getElementById('DocumentNumber');
    const issuedDisplay = document.getElementById('IssuedDateDisplay');
    const expiryDisplay = document.getElementById('ExpiryDateDisplay');
    const issuedHidden = document.getElementById('IssuedDate');
    const expiryHidden = document.getElementById('ExpiryDate');
    const issuedError = document.getElementById('IssuedDateClientError');
    const expiryError = document.getElementById('ExpiryDateClientError');
    const expiryRequiredMark = document.getElementById('ExpiryRequiredMark');

    const resetFile = () => {
        if (!input || !image || !preview || !prompt) return;
        input.value = '';
        image.removeAttribute('src');
        preview.hidden = true;
        prompt.hidden = false;
    };

    const showFile = file => {
        if (!file) return resetFile();
        if (!name || !size || !image || !preview || !prompt) return;
        name.textContent = file.name;
        size.textContent = `${(file.size / (1024 * 1024)).toFixed(2)} MB`;
        const reader = new FileReader();
        reader.onload = event => image.src = event.target.result;
        reader.readAsDataURL(file);
        prompt.hidden = true;
        preview.hidden = false;
    };

    input?.addEventListener('change', () => showFile(input.files?.[0]));
    remove?.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        resetFile();
    });

    const placeholders = {
        Registration: 'Ví dụ: DK-30A-12345',
        Inspection: 'Ví dụ: KD-2901V-12345',
        Insurance: 'Ví dụ: BH-30A-12345',
        RoadFee: 'Ví dụ: PDB-30A-12345',
        Other: 'Ví dụ: GT-30A-12345'
    };

    const updateDocumentTypeUi = () => {
        if (documentType && documentNumber) {
            documentNumber.placeholder = placeholders[documentType.value] || 'Ví dụ: DK-30A-12345';
        }
        if (expiryRequiredMark && documentType) {
            const expiryRequired = ['Inspection', 'Insurance', 'RoadFee'].includes(documentType.value);
            expiryRequiredMark.hidden = !expiryRequired;
        }
    };

    documentType?.addEventListener('change', updateDocumentTypeUi);
    updateDocumentTypeUi();

    const digitsOnlyDateMask = inputElement => {
        if (!inputElement) return;
        inputElement.addEventListener('input', () => {
            const digits = inputElement.value.replace(/\D/g, '').slice(0, 8);
            let formatted = digits;
            if (digits.length > 4) {
                formatted = `${digits.slice(0, 2)}/${digits.slice(2, 4)}/${digits.slice(4)}`;
            } else if (digits.length > 2) {
                formatted = `${digits.slice(0, 2)}/${digits.slice(2)}`;
            }
            inputElement.value = formatted;
        });
    };

    digitsOnlyDateMask(issuedDisplay);
    digitsOnlyDateMask(expiryDisplay);

    const parseDisplayDate = value => {
        if (!value) return null;
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value.trim());
        if (!match) return undefined;
        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);
        if (
            date.getFullYear() !== year ||
            date.getMonth() !== month - 1 ||
            date.getDate() !== day
        ) {
            return undefined;
        }
        return date;
    };

    const toIsoDate = date => {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        return `${year}-${month}-${day}`;
    };

    const setError = (element, message) => {
        if (!element) return;
        element.textContent = message || '';
        element.hidden = !message;
    };

    const validateDates = () => {
        if (!issuedDisplay || !expiryDisplay || !issuedHidden || !expiryHidden) return true;

        setError(issuedError, '');
        setError(expiryError, '');
        issuedDisplay.classList.remove('is-invalid');
        expiryDisplay.classList.remove('is-invalid');

        const issued = parseDisplayDate(issuedDisplay.value);
        const expiry = parseDisplayDate(expiryDisplay.value);
        let valid = true;

        if (!issuedDisplay.value.trim()) {
            setError(issuedError, 'Vui lòng nhập ngày cấp theo định dạng dd/mm/yyyy.');
            issuedDisplay.classList.add('is-invalid');
            valid = false;
        } else if (issued === undefined) {
            setError(issuedError, 'Ngày cấp không hợp lệ. Ví dụ đúng: 16/08/2026.');
            issuedDisplay.classList.add('is-invalid');
            valid = false;
        } else {
            issuedHidden.value = toIsoDate(issued);
            const today = new Date();
            today.setHours(0, 0, 0, 0);
            if (issued > today) {
                setError(issuedError, 'Ngày cấp không được ở tương lai.');
                issuedDisplay.classList.add('is-invalid');
                valid = false;
            }
        }

        const expiryRequired = documentType && ['Inspection', 'Insurance', 'RoadFee'].includes(documentType.value);

        if (!expiryDisplay.value.trim()) {
            expiryHidden.value = '';
            if (expiryRequired) {
                setError(expiryError, 'Vui lòng nhập ngày hết hạn cho loại giấy tờ này.');
                expiryDisplay.classList.add('is-invalid');
                valid = false;
            }
        } else if (expiry === undefined) {
            setError(expiryError, 'Ngày hết hạn không hợp lệ. Ví dụ đúng: 16/08/2027.');
            expiryDisplay.classList.add('is-invalid');
            valid = false;
        } else {
            expiryHidden.value = toIsoDate(expiry);
            if (issued instanceof Date && expiry <= issued) {
                setError(expiryError, 'Ngày hết hạn phải sau ngày cấp.');
                expiryDisplay.classList.add('is-invalid');
                valid = false;
            }
        }

        return valid;
    };

    issuedDisplay?.addEventListener('blur', validateDates);
    expiryDisplay?.addEventListener('blur', validateDates);

    form?.addEventListener('submit', event => {
        if (!validateDates()) {
            event.preventDefault();
            const firstInvalid = form.querySelector('.is-invalid');
            firstInvalid?.focus();
        }
    });
})();
