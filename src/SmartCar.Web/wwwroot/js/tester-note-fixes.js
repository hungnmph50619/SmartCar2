document.addEventListener('DOMContentLoaded', () => {
    const timeInputIds = ['handover-time', 'return-time', 'requested-return-time', 'admin-requested-return-time'];

    timeInputIds.forEach(id => {
        const input = document.getElementById(id);
        if (!(input instanceof HTMLInputElement)) return;
        const currentValue = input.value;
        input.type = 'time';
        input.step = '60';
        input.removeAttribute('inputmode');
        input.removeAttribute('maxlength');
        input.removeAttribute('pattern');
        input.placeholder = '';
        if (currentValue) input.value = currentValue;
    });

    const fuelInputs = [
        document.getElementById('handover-fuel'),
        document.querySelector('form[action*="Returns"] input[name="FuelLevel"]')
    ].filter(input => input instanceof HTMLInputElement);

    fuelInputs.forEach(input => {
        input.type = 'number';
        input.min = '0';
        input.max = '100';
        input.step = '1';
        input.inputMode = 'numeric';

        const normalize = () => {
            const raw = String(input.value || '').replace(/[^0-9]/g, '').slice(0, 3);
            if (!raw) {
                input.value = '';
                input.setCustomValidity('');
                return;
            }

            const value = Math.min(100, Math.max(0, Number(raw)));
            input.value = String(value);
            input.setCustomValidity('');
        };

        input.addEventListener('input', normalize);
        input.addEventListener('change', normalize);
        input.addEventListener('paste', () => window.setTimeout(normalize, 0));
    });

    localizeSignedFileValidationMessages();
    polishSignedDocumentUi();
    initializeForceMajeureEvidenceUi();
    normalizeSavedForceMajeureEvidence();
});

function localizeSignedFileValidationMessages() {
    const signedFileInputs = document.querySelectorAll(
        '.signed-file-input, #signed-documents, input[type="file"][name="signedDocuments"], input[type="file"][name="signedDocument"]'
    );
    const requiredMessage = 'Vui lòng chọn ít nhất một ảnh bản ký.';

    signedFileInputs.forEach(input => {
        if (!(input instanceof HTMLInputElement)) return;

        const refreshMessage = () => {
            if (input.files && input.files.length > 0) {
                input.setCustomValidity('');
            } else {
                input.setCustomValidity(requiredMessage);
            }
        };

        input.addEventListener('invalid', refreshMessage);
        input.addEventListener('change', refreshMessage);
        input.form?.addEventListener('submit', refreshMessage);
        refreshMessage();
    });
}

function polishSignedDocumentUi() {
    const path = window.location.pathname;
    const isCustomerDetails = /^\/Bookings\/Details(?:\/|$)/i.test(path);
    const isAdminInspection = /^\/Returns\/Inspect(?:\/|$)/i.test(path);

    if (!isCustomerDetails && !isAdminInspection) {
        return;
    }

    document.querySelectorAll('.card').forEach(card => {
        const header = card.querySelector(':scope > .card-header');
        const body = card.querySelector(':scope > .card-body');
        const title = header?.querySelector('strong')?.textContent?.trim();

        if (!header || !body || !title || !['Biên bản giao xe', 'Biên bản trả xe'].includes(title)) {
            return;
        }

        normalizeSignedBlock(header, body, title);
        card.classList.add('customer-record-card');
    });

    if (isAdminInspection) {
        normalizeAdminInspectionPanel(document.getElementById('evidence-handover'));
        normalizeAdminInspectionPanel(document.getElementById('evidence-return'));

        document.querySelectorAll('a.btn, button.btn').forEach(button => {
            const text = button.textContent?.trim();
            if (text === 'In') {
                button.textContent = 'In biên bản';
            }
            if (text === 'Sửa') {
                button.textContent = 'Sửa biên bản';
            }
        });
    }
}

function normalizeAdminInspectionPanel(panel) {
    if (!(panel instanceof HTMLElement)) return;
    const box = panel.querySelector(':scope > .border');
    if (!box) return;

    const title = box.querySelector('h2')?.textContent?.trim();
    if (!title || !['Biên bản giao', 'Biên bản trả'].includes(title)) return;

    normalizeSignedBlock(box, box, title);
}

function normalizeSignedBlock(headerRoot, bodyRoot, title) {
    const isHandover = title.includes('giao');
    const signedSelector = isHandover
        ? 'a[href*="signed-handover-"]'
        : 'a[href*="signed-return-"]';
    const signedLinks = Array.from(bodyRoot.querySelectorAll(signedSelector));
    const signedCount = signedLinks.length;
    const badge = headerRoot.querySelector('.badge');

    if (badge) {
        badge.className = signedCount > 0 ? 'badge bg-success' : 'badge bg-danger';
        badge.textContent = signedCount > 0 ? 'Đã ký' : 'Thiếu ký';
    }

    signedLinks.forEach((link, index) => {
        link.className = 'btn btn-sm btn-outline-success text-decoration-none';
        link.textContent = `Trang ${index + 1}`;
    });

    bodyRoot.querySelectorAll('strong').forEach(strong => {
        const text = strong.textContent?.trim() ?? '';
        if (text === 'Bản ký biên bản giao:' || text === 'Bản ký biên bản trả:' || text.startsWith('Bản ký ')) {
            strong.textContent = signedCount > 0 ? `Bản ký (${signedCount} trang):` : 'Bản ký:';
        }
    });

    bodyRoot.querySelectorAll('.col-6.col-md-4').forEach(column => {
        if (!column.querySelector('img')) return;
        column.querySelectorAll(':scope > .small.fw-semibold.mb-1').forEach(label => label.remove());
    });
}

function initializeForceMajeureEvidenceUi() {
    setupAdminPhoneExtensionForm();
    setupEvidenceFileInputs();
    setupLocationControls();
}

function setupAdminPhoneExtensionForm() {
    const form = document.getElementById('admin-extension-phone-form');
    if (!(form instanceof HTMLFormElement)) return;

    form.action = '/AdminExtensionRequests/CreateForCustomer';

    const locationInput = form.querySelector('input[name="customerLiveLocation"]');
    if (locationInput instanceof HTMLInputElement) {
        const column = locationInput.closest('.col-md-3') || locationInput.parentElement;
        locationInput.type = 'hidden';
        locationInput.readOnly = true;
        locationInput.placeholder = '';

        if (column && column.dataset.smartLocationColumnReady !== 'true') {
            column.dataset.smartLocationColumnReady = 'true';
            column.innerHTML = '';

            const label = document.createElement('label');
            label.className = 'form-label';
            label.textContent = 'Vị trí hiện tại';

            const hiddenLegacy = document.createElement('input');
            hiddenLegacy.type = 'hidden';
            hiddenLegacy.name = 'customerLiveLocation';

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-outline-primary w-100 js-smart-current-location';
            button.textContent = 'Lấy vị trí hiện tại';

            const status = document.createElement('div');
            status.className = 'form-text js-smart-location-status';
            status.textContent = 'Chưa lấy vị trí.';

            column.append(label, hiddenLegacy, button, status);
        }
    }
}

function setupEvidenceFileInputs() {
    const inputs = Array.from(document.querySelectorAll('input[type="file"][name="evidenceImage"], input[type="file"][name="evidenceImages"]'))
        .filter(input => input instanceof HTMLInputElement)
        .filter(input => {
            const form = input.form;
            return form && (
                form.querySelector('[name="evidenceNote"]') ||
                form.action.includes('Extensions') ||
                form.action.includes('AdminExtensionRequests'));
        });

    inputs.forEach(input => {
        if (input.dataset.smartEvidenceReady === 'true') return;
        input.dataset.smartEvidenceReady = 'true';
        input.name = 'evidenceImages';
        input.multiple = true;
        input.accept = 'image/jpeg,image/png,image/webp';

        const helpText = input.parentElement?.querySelector('.form-text');
        if (helpText) {
            helpText.textContent = 'Chọn 2-8 ảnh JPG/PNG/WEBP, tối đa 5 MB/ảnh.';
        }

        const preview = document.createElement('div');
        preview.className = 'smart-evidence-preview row g-2 mt-2';
        preview.setAttribute('aria-live', 'polite');
        input.insertAdjacentElement('afterend', preview);

        const render = () => renderEvidencePreview(input, preview);
        const validate = () => validateEvidenceInput(input, false);

        input.addEventListener('change', () => {
            normalizeEvidenceFileSelection(input);
            validate();
            render();
        });

        input.addEventListener('invalid', () => {
            validateEvidenceInput(input, true);
        });

        input.form?.addEventListener('submit', event => {
            if (!validateEvidenceInput(input, true)) {
                event.preventDefault();
                input.reportValidity();
                return;
            }

            if (!validateEvidenceLocation(input.form, input)) {
                event.preventDefault();
                return;
            }
        });
    });
}

function validateEvidenceInput(input, showMessage) {
    const requiresEvidence = forceMajeureEvidenceIsRequired(input.form, input);
    const count = input.files?.length || 0;
    let message = '';

    if (requiresEvidence && count < 2) {
        message = 'Vui lòng chọn tối thiểu 2 ảnh minh chứng.';
    } else if (count > 8) {
        message = 'Chỉ được chọn tối đa 8 ảnh minh chứng.';
    }

    input.setCustomValidity(message);
    if (message && showMessage) input.reportValidity();
    return message === '';
}

function validateEvidenceLocation(form, input) {
    if (!forceMajeureEvidenceIsRequired(form, input)) return true;
    const latitude = form.querySelector('input[name="evidenceLatitude"]')?.value?.trim();
    const longitude = form.querySelector('input[name="evidenceLongitude"]')?.value?.trim();
    if (latitude && longitude) return true;

    const button = form.querySelector('.js-smart-current-location, #extension-get-location, .js-extension-location');
    const status = form.querySelector('.js-smart-location-status, .js-location-status, #extension-location-status');
    if (status) {
        status.textContent = 'Vui lòng bấm Lấy vị trí hiện tại trước khi gửi yêu cầu.';
        status.classList.add('text-danger');
    }
    if (button instanceof HTMLElement) {
        button.focus();
        button.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    alert('Vui lòng bấm Lấy vị trí hiện tại trước khi gửi yêu cầu bất khả kháng.');
    return false;
}

function forceMajeureEvidenceIsRequired(form, input) {
    if (!(form instanceof HTMLFormElement)) return false;
    if (form.querySelector('input[name="extensionId"]')) return true;
    const checkbox = form.querySelector('input[name="isForceMajeure"]');
    if (checkbox instanceof HTMLInputElement && checkbox.checked) return true;
    const evidenceBlock = input.closest('#force-majeure-evidence');
    return evidenceBlock instanceof HTMLElement && !evidenceBlock.classList.contains('d-none');
}

function normalizeEvidenceFileSelection(input) {
    const files = Array.from(input.files || []);
    const validFiles = [];
    let message = '';

    for (const file of files) {
        const extensionOk = /\.(jpe?g|png|webp)$/i.test(file.name || '');
        const typeOk = !file.type || ['image/jpeg', 'image/png', 'image/webp'].includes(file.type);
        if (!extensionOk || !typeOk) {
            message = 'Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.';
            continue;
        }

        if (file.size > 5 * 1024 * 1024) {
            message = 'Mỗi ảnh minh chứng tối đa 5 MB.';
            continue;
        }

        validFiles.push(file);
    }

    const finalFiles = validFiles.slice(0, 8);
    if (validFiles.length > 8) {
        message = 'Chỉ được chọn tối đa 8 ảnh minh chứng.';
    }

    assignFiles(input, finalFiles);
    input.setCustomValidity(message);
    if (message) input.reportValidity();
}

function assignFiles(input, files) {
    if (typeof DataTransfer === 'undefined') return;
    const transfer = new DataTransfer();
    files.forEach(file => transfer.items.add(file));
    input.files = transfer.files;
}

function renderEvidencePreview(input, preview) {
    preview.innerHTML = '';
    const files = Array.from(input.files || []);

    if (files.length === 0) {
        return;
    }

    files.forEach((file, index) => {
        const column = document.createElement('div');
        column.className = 'col-6 col-md-3';

        const card = document.createElement('div');
        card.className = 'position-relative border rounded bg-white p-1 h-100';

        const img = document.createElement('img');
        img.src = URL.createObjectURL(file);
        img.alt = `Ảnh minh chứng ${index + 1}`;
        img.className = 'img-fluid rounded w-100';
        img.style.height = '96px';
        img.style.objectFit = 'cover';
        img.addEventListener('load', () => URL.revokeObjectURL(img.src), { once: true });

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn btn-sm btn-danger position-absolute top-0 end-0 rounded-circle px-2 py-0';
        remove.style.transform = 'translate(35%, -35%)';
        remove.setAttribute('aria-label', `Xóa ảnh ${index + 1}`);
        remove.textContent = '×';
        remove.addEventListener('click', () => {
            const remaining = Array.from(input.files || []).filter((_, fileIndex) => fileIndex !== index);
            assignFiles(input, remaining);
            validateEvidenceInput(input, false);
            renderEvidencePreview(input, preview);
        });

        const caption = document.createElement('div');
        caption.className = 'small text-truncate mt-1';
        caption.title = file.name;
        caption.textContent = `Ảnh ${index + 1}: ${file.name}`;

        card.append(img, remove, caption);
        column.appendChild(card);
        preview.appendChild(column);
    });
}

function setupLocationControls() {
    document.querySelectorAll('form').forEach(form => ensureEvidenceLocationFields(form));

    const mainButton = document.getElementById('extension-get-location');
    if (mainButton instanceof HTMLButtonElement) {
        mainButton.classList.add('js-smart-current-location');
    }

    document.querySelectorAll('.js-extension-location').forEach(button => {
        if (button instanceof HTMLButtonElement) {
            button.classList.add('js-smart-current-location');
        }
    });

    document.querySelectorAll('.js-smart-current-location').forEach(button => {
        const form = button.closest('form');
        if (form instanceof HTMLFormElement) ensureEvidenceLocationFields(form);
        ensureLocationPreviewContainer(button);
    });

    document.addEventListener('click', event => {
        const button = event.target.closest('.js-smart-current-location');
        if (!(button instanceof HTMLButtonElement)) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        requestCurrentLocation(button);
    }, true);
}

function ensureEvidenceLocationFields(form) {
    if (!(form instanceof HTMLFormElement)) return;
    ensureHiddenInput(form, 'evidenceLatitude');
    ensureHiddenInput(form, 'evidenceLongitude');
    ensureHiddenInput(form, 'evidencePlaceName');
}

function ensureHiddenInput(form, name) {
    let input = form.querySelector(`input[name="${name}"]`);
    if (input instanceof HTMLInputElement) return input;

    input = document.createElement('input');
    input.type = 'hidden';
    input.name = name;
    form.appendChild(input);
    return input;
}

function ensureLocationPreviewContainer(button) {
    if (button.dataset.smartLocationPreviewReady === 'true') return;
    button.dataset.smartLocationPreviewReady = 'true';

    const host = button.closest('.col-md-6, .col-md-3, .col-12, form') || button.parentElement;
    if (!host || host.querySelector(':scope > .smart-location-preview')) return;

    const preview = document.createElement('div');
    preview.className = 'smart-location-preview mt-2 d-none';
    host.appendChild(preview);
}

async function requestCurrentLocation(button) {
    const form = button.closest('form');
    if (!(form instanceof HTMLFormElement)) return;

    ensureEvidenceLocationFields(form);

    const status = button.parentElement?.querySelector('.js-smart-location-status') ||
        button.parentElement?.querySelector('.js-location-status') ||
        document.getElementById('extension-location-status') ||
        button.closest('.col-md-6, .col-md-3, .col-12')?.querySelector('.form-text');

    if (!navigator.geolocation) {
        if (status) status.textContent = 'Thiết bị không hỗ trợ định vị.';
        return;
    }

    button.disabled = true;
    const originalText = button.textContent;
    button.textContent = 'Đang lấy vị trí...';
    if (status) {
        status.textContent = 'Đang lấy vị trí hiện tại...';
        status.classList.remove('text-danger');
    }

    navigator.geolocation.getCurrentPosition(async position => {
        const latitude = position.coords.latitude;
        const longitude = position.coords.longitude;
        const placeName = await reverseGeocodePlace(latitude, longitude);

        setLocationFields(form, latitude, longitude, placeName);
        renderLocationPreview(button, latitude, longitude, placeName);

        if (status) {
            status.textContent = placeName
                ? `Đã lấy vị trí: ${placeName}`
                : 'Đã lấy vị trí hiện tại.';
            status.classList.remove('text-danger');
        }

        button.disabled = false;
        button.textContent = originalText || 'Lấy vị trí hiện tại';
    }, () => {
        if (status) {
            status.textContent = 'Không lấy được vị trí; hãy cấp quyền vị trí rồi thử lại.';
            status.classList.add('text-danger');
        }
        button.disabled = false;
        button.textContent = originalText || 'Lấy vị trí hiện tại';
    }, { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 });
}

function setLocationFields(form, latitude, longitude, placeName) {
    const latitudeInput = ensureHiddenInput(form, 'evidenceLatitude');
    const longitudeInput = ensureHiddenInput(form, 'evidenceLongitude');
    const placeInput = ensureHiddenInput(form, 'evidencePlaceName');
    const legacy = form.querySelector('input[name="customerLiveLocation"]');

    latitudeInput.value = latitude.toFixed(6);
    longitudeInput.value = longitude.toFixed(6);
    placeInput.value = placeName || '';
    if (legacy instanceof HTMLInputElement) {
        legacy.value = `${latitude.toFixed(6)}, ${longitude.toFixed(6)}`;
    }
}

async function reverseGeocodePlace(latitude, longitude) {
    const local = await tryFetchJson(`/api/geocoding/reverse?lat=${encodeURIComponent(latitude)}&lon=${encodeURIComponent(longitude)}`);
    const localName = extractPlaceName(local);
    if (localName) return localName;

    const nominatimUrl = `https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=${encodeURIComponent(latitude)}&lon=${encodeURIComponent(longitude)}&zoom=18&addressdetails=1`;
    const nominatim = await tryFetchJson(nominatimUrl);
    return extractPlaceName(nominatim);
}

async function tryFetchJson(url) {
    try {
        const response = await fetch(url, { headers: { 'Accept': 'application/json' } });
        if (!response.ok) return null;
        return await response.json();
    } catch {
        return null;
    }
}

function extractPlaceName(data) {
    if (!data) return '';
    return data.display_name || data.displayName || data.name || data.formattedAddress || formatAddressParts(data.address) || '';
}

function formatAddressParts(address) {
    if (!address || typeof address !== 'object') return '';
    return [
        address.road,
        address.suburb || address.ward,
        address.city || address.town || address.county,
        address.state
    ].filter(Boolean).join(', ');
}

function renderLocationPreview(button, latitude, longitude, placeName) {
    const host = button.closest('.col-md-6, .col-md-3, .col-12, form') || button.parentElement;
    if (!host) return;

    let preview = host.querySelector(':scope > .smart-location-preview');
    if (!preview) {
        preview = document.createElement('div');
        preview.className = 'smart-location-preview mt-2';
        host.appendChild(preview);
    }

    preview.classList.remove('d-none');
    preview.innerHTML = '';

    const delta = 0.004;
    const bbox = [longitude - delta, latitude - delta, longitude + delta, latitude + delta].join(',');
    const mapUrl = `https://www.openstreetmap.org/export/embed.html?bbox=${encodeURIComponent(bbox)}&layer=mapnik&marker=${encodeURIComponent(`${latitude},${longitude}`)}`;

    const info = document.createElement('div');
    info.className = 'small border rounded bg-white p-2 mb-2';
    info.innerHTML = `<strong>Vị trí hiện tại:</strong> ${latitude.toFixed(6)}, ${longitude.toFixed(6)}${placeName ? `<br><strong>Địa điểm:</strong> ${escapeHtml(placeName)}` : ''}`;

    const iframe = document.createElement('iframe');
    iframe.title = 'Bản đồ vị trí hiện tại';
    iframe.src = mapUrl;
    iframe.className = 'w-100 rounded border';
    iframe.style.height = '150px';
    iframe.loading = 'lazy';

    preview.append(info, iframe);
}

function normalizeSavedForceMajeureEvidence() {
    document.querySelectorAll('img[src*="/uploads/extensions/"]').forEach(img => {
        const raw = img.getAttribute('src') || img.closest('a')?.getAttribute('href') || '';
        const paths = splitEvidencePaths(raw);
        if (paths.length <= 1) return;

        const container = img.closest('a')?.parentElement || img.parentElement;
        if (!container || container.dataset.smartEvidenceDisplayReady === 'true') return;
        container.dataset.smartEvidenceDisplayReady = 'true';
        container.innerHTML = '';
        container.appendChild(buildSavedEvidenceGrid(paths));
    });

    document.querySelectorAll('a[href*="/uploads/extensions/"]').forEach(anchor => {
        if (anchor.querySelector('img')) return;
        const paths = splitEvidencePaths(anchor.getAttribute('href') || '');
        if (paths.length <= 1) return;

        const container = anchor.parentElement;
        if (!container || container.dataset.smartEvidenceDisplayReady === 'true') return;
        container.dataset.smartEvidenceDisplayReady = 'true';
        container.innerHTML = '';
        container.appendChild(buildSavedEvidenceGrid(paths));
    });

    document.querySelectorAll('.border, .mt-2, .card-body').forEach(block => {
        if (!(block instanceof HTMLElement) || block.dataset.smartEvidenceLocationReady === 'true') return;
        if (block.closest('form')) return;

        const match = block.textContent?.match(/Vị trí trực tiếp:\s*(-?\d+(?:[.,]\d+)?)\s*,\s*(-?\d+(?:[.,]\d+)?)/i);
        if (!match) return;

        const latitude = Number(match[1].replace(',', '.'));
        const longitude = Number(match[2].replace(',', '.'));
        if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return;

        const placeMatch = block.textContent?.match(/Địa điểm:\s*([^|]+)/i);
        const placeName = placeMatch ? placeMatch[1].trim() : '';
        block.dataset.smartEvidenceLocationReady = 'true';
        renderSavedLocationMap(block, latitude, longitude, placeName);
    });
}

function splitEvidencePaths(raw) {
    return String(raw || '')
        .split(';')
        .map(path => path.trim())
        .filter(path => path.startsWith('/uploads/extensions/') || path.includes('/uploads/extensions/'))
        .map(path => path.replace(window.location.origin, ''));
}

function buildSavedEvidenceGrid(paths) {
    const grid = document.createElement('div');
    grid.className = 'row g-2 mt-2';

    paths.forEach((path, index) => {
        const column = document.createElement('div');
        column.className = 'col-6 col-md-4';

        const link = document.createElement('a');
        link.href = path;
        link.target = '_blank';
        link.rel = 'noopener';
        link.className = 'd-block text-decoration-none';

        const img = document.createElement('img');
        img.src = path;
        img.alt = `Ảnh minh chứng ${index + 1}`;
        img.className = 'img-thumbnail w-100';
        img.style.height = '130px';
        img.style.objectFit = 'cover';

        const caption = document.createElement('div');
        caption.className = 'small text-muted mt-1';
        caption.textContent = `Ảnh ${index + 1}`;

        link.append(img, caption);
        column.appendChild(link);
        grid.appendChild(column);
    });

    return grid;
}

function renderSavedLocationMap(block, latitude, longitude, placeName) {
    const delta = 0.004;
    const bbox = [longitude - delta, latitude - delta, longitude + delta, latitude + delta].join(',');
    const mapUrl = `https://www.openstreetmap.org/export/embed.html?bbox=${encodeURIComponent(bbox)}&layer=mapnik&marker=${encodeURIComponent(`${latitude},${longitude}`)}`;

    const wrapper = document.createElement('div');
    wrapper.className = 'mt-2';
    wrapper.innerHTML = `
        <div class="small border rounded bg-light p-2 mb-2">
            <strong>Tọa độ:</strong> ${latitude.toFixed(6)}, ${longitude.toFixed(6)}${placeName ? `<br><strong>Địa điểm:</strong> ${escapeHtml(placeName)}` : ''}
        </div>
        <iframe title="Bản đồ vị trí minh chứng" class="w-100 rounded border" style="height:150px" loading="lazy" src="${mapUrl}"></iframe>`;

    block.appendChild(wrapper);
}

function escapeHtml(value) {
    return String(value || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}
