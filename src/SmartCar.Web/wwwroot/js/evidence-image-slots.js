(() => {
    const maximumImageBytes = 5 * 1024 * 1024;
    const allowedExtensions = ['.jpg', '.jpeg', '.png', '.webp'];
    const allowedMimeTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);

    // Razor boolean attributes such as selected="False" are still truthy HTML attributes.
    const accessoryMode = document.querySelector('[data-accessory-mode]');
    const accessoryValue = document.querySelector('[data-accessory-value]');
    if (accessoryMode instanceof HTMLSelectElement && accessoryValue instanceof HTMLInputElement) {
        accessoryMode.value = accessoryValue.value.trim().toLowerCase().startsWith('thiếu/mất:')
            ? 'missing'
            : 'Đủ';
    }

    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('[data-evidence-slot]').forEach(initializeSlot);
        document.querySelectorAll('[data-evidence-multiple]').forEach(initializeMultiple);
        document.querySelectorAll('form').forEach((form) => {
            if (form.querySelector('[data-evidence-input], [data-evidence-multiple-input]')) {
                form.addEventListener('submit', async (event) => {
                    const okay = await validateDuplicateContent(form);
                    if (!okay) {
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        form.querySelector(':invalid')?.reportValidity();
                    }
                }, true);
            }
        });
    });

    function initializeSlot(slot) {
        const input = slot.querySelector('[data-evidence-input]');
        const preview = slot.querySelector('[data-evidence-preview]');
        const name = slot.querySelector('[data-evidence-name]');
        let objectUrl = null;

        if (!(input instanceof HTMLInputElement)) return;
        input.dataset.evidenceLabel = resolveLabel(slot, input);

        const actions = ensureActions(slot);
        const removeButton = document.createElement('button');
        removeButton.type = 'button';
        removeButton.className = 'btn btn-outline-danger d-none';
        removeButton.textContent = 'Xóa ảnh';
        actions.appendChild(removeButton);

        const render = () => {
            if (objectUrl) {
                URL.revokeObjectURL(objectUrl);
                objectUrl = null;
            }

            const file = input.files?.[0] || null;
            clearInputError(input, slot);

            if (file) {
                const error = validateOneFile(file);
                if (error) {
                    setInputError(input, slot, error);
                }
            }

            slot.classList.toggle('border-success', Boolean(file) && input.validationMessage === '');
            if (name) name.textContent = file ? `${file.name} · ${formatBytes(file.size)}` : 'Chưa chọn ảnh';
            removeButton.classList.toggle('d-none', !file);

            if (!preview) return;
            preview.style.objectFit = 'contain';
            if (!file) {
                preview.removeAttribute('src');
                preview.classList.add('d-none');
                return;
            }

            objectUrl = URL.createObjectURL(file);
            preview.src = objectUrl;
            preview.classList.remove('d-none');
        };

        removeButton.addEventListener('click', () => {
            input.value = '';
            input.dispatchEvent(new Event('change', { bubbles: true }));
        });

        input.addEventListener('change', async () => {
            render();
            await validateDuplicateContent(input.form);
        });
        render();
    }

    function initializeMultiple(root) {
        const input = root.querySelector('[data-evidence-multiple-input]');
        const preview = root.querySelector('[data-evidence-multiple-preview]');
        const count = root.querySelector('[data-evidence-count]');
        let objectUrls = [];

        if (!(input instanceof HTMLInputElement)) return;
        input.dataset.evidenceLabel = resolveLabel(root, input);

        const render = () => {
            objectUrls.forEach(URL.revokeObjectURL);
            objectUrls = [];
            const files = Array.from(input.files || []);
            clearInputError(input, root);

            const invalid = files.find((file) => validateOneFile(file));
            if (invalid) {
                setInputError(input, root, `${invalid.name}: ${validateOneFile(invalid)}`);
            }

            if (count) count.textContent = files.length ? `Đã chọn ${files.length} ảnh` : 'Chưa chọn ảnh';
            if (!preview) return;
            preview.innerHTML = '';

            files.forEach((file, index) => {
                const url = URL.createObjectURL(file);
                objectUrls.push(url);
                const card = document.createElement('div');
                card.className = 'border rounded-3 bg-body p-1';
                card.style.width = '132px';

                const image = document.createElement('img');
                image.src = url;
                image.alt = file.name;
                image.className = 'rounded-2 border bg-dark w-100';
                image.style.height = '92px';
                image.style.objectFit = 'contain';

                const caption = document.createElement('div');
                caption.className = 'small text-muted text-truncate mt-1';
                caption.title = file.name;
                caption.textContent = file.name;

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'btn btn-sm btn-outline-danger w-100 mt-1';
                remove.textContent = 'Xóa';
                remove.addEventListener('click', () => {
                    const transfer = new DataTransfer();
                    files.forEach((candidate, candidateIndex) => {
                        if (candidateIndex !== index) transfer.items.add(candidate);
                    });
                    input.files = transfer.files;
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                });

                card.append(image, caption, remove);
                preview.appendChild(card);
            });
        };

        input.addEventListener('change', async () => {
            render();
            await validateDuplicateContent(input.form);
        });
        render();
    }

    async function validateDuplicateContent(form) {
        if (!(form instanceof HTMLFormElement)) return true;
        const inputs = Array.from(form.querySelectorAll('[data-evidence-input], [data-evidence-multiple-input]'))
            .filter((input) => input instanceof HTMLInputElement);

        inputs.forEach((input) => {
            const host = input.closest('[data-evidence-slot], [data-evidence-multiple]');
            if (host && /Ảnh này đã được dùng/i.test(input.validationMessage)) {
                clearInputError(input, host);
            }
        });

        const entries = [];
        for (const input of inputs) {
            const files = Array.from(input.files || []);
            for (const file of files) {
                if (!file || file.size <= 0) continue;
                entries.push({ input, file, hash: await hashFile(file) });
            }
        }

        const groups = new Map();
        entries.forEach((entry) => {
            const list = groups.get(entry.hash) || [];
            list.push(entry);
            groups.set(entry.hash, list);
        });

        let valid = true;
        groups.forEach((group) => {
            if (group.length < 2) return;
            valid = false;
            const labels = group.map((item) => item.input.dataset.evidenceLabel || item.file.name);
            group.forEach((entry) => {
                const other = labels.filter((label) => label !== (entry.input.dataset.evidenceLabel || entry.file.name));
                const host = entry.input.closest('[data-evidence-slot], [data-evidence-multiple]');
                if (host) {
                    setInputError(entry.input, host, `Ảnh này đã được dùng ở ${other.join(', ') || 'một vị trí khác'}. Mỗi vị trí phải có ảnh riêng.`);
                }
            });
        });

        return valid && inputs.every((input) => input.checkValidity());
    }

    async function hashFile(file) {
        const buffer = await file.arrayBuffer();
        if (crypto?.subtle) {
            const digest = await crypto.subtle.digest('SHA-256', buffer);
            return Array.from(new Uint8Array(digest)).map((value) => value.toString(16).padStart(2, '0')).join('');
        }
        return `${file.name}:${file.size}:${file.lastModified}`;
    }

    function validateOneFile(file) {
        const lower = file.name.toLowerCase();
        const extensionOkay = allowedExtensions.some((extension) => lower.endsWith(extension));
        const mimeOkay = !file.type || allowedMimeTypes.has(file.type.toLowerCase());
        if (!extensionOkay || !mimeOkay) return 'Chỉ chấp nhận JPG, PNG hoặc WEBP.';
        if (file.size > maximumImageBytes) return 'Ảnh không được vượt quá 5 MB.';
        return null;
    }

    function setInputError(input, host, message) {
        input.setCustomValidity(message);
        host.classList.remove('border-success');
        host.classList.add('border-danger');
        let error = host.querySelector(':scope > .staff-evidence-error');
        if (!error) {
            error = document.createElement('div');
            error.className = 'staff-evidence-error';
            host.appendChild(error);
        }
        error.textContent = message;
    }

    function clearInputError(input, host) {
        input.setCustomValidity('');
        host.classList.remove('border-danger');
        host.querySelector(':scope > .staff-evidence-error')?.remove();
    }

    function ensureActions(host) {
        let actions = host.querySelector(':scope > .staff-file-actions');
        if (!actions) {
            actions = document.createElement('div');
            actions.className = 'staff-file-actions';
            host.appendChild(actions);
        }
        return actions;
    }

    function resolveLabel(host, input) {
        const strong = host.querySelector('strong');
        const label = strong?.textContent?.replace('*', '').trim();
        return label || input.name || 'Ảnh';
    }

    function formatBytes(bytes) {
        if (!Number.isFinite(bytes) || bytes <= 0) return '0 KB';
        if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
        return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
    }
})();
