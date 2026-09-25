(() => {
    const draftDbName = 'smartcar-form-drafts';
    const draftStoreName = 'files';
    const draftMaxAgeMs = 60 * 60 * 1000;

    document.addEventListener('DOMContentLoaded', () => {
        initializeBoundedNumericInputs();
        initializeFileDrafts().catch(() => {
            // IndexedDB có thể bị tắt ở private mode. Form vẫn hoạt động nhưng không thể phục hồi file cục bộ.
        });
    });

    function initializeBoundedNumericInputs() {
        document.querySelectorAll('input[type="number"]').forEach((input) => {
            if (!(input instanceof HTMLInputElement)) return;

            const min = parseFinite(input.min);
            const max = parseFinite(input.max);
            const integerOnly = input.dataset.integer === 'true' || input.step === '1';
            const maxDigits = resolveMaxDigits(input, max);
            let lastAcceptedValue = normalizeInitialValue(input.value, min, max, integerOnly, maxDigits);

            if (lastAcceptedValue !== input.value) {
                input.value = lastAcceptedValue;
            }

            input.inputMode = integerOnly ? 'numeric' : 'decimal';

            input.addEventListener('keydown', (event) => {
                if (event.ctrlKey || event.metaKey || event.altKey) return;

                if (['e', 'E', '+'].includes(event.key)) {
                    event.preventDefault();
                    return;
                }
                if (min !== null && min >= 0 && event.key === '-') {
                    event.preventDefault();
                    return;
                }
                if (integerOnly && (event.key === '.' || event.key === ',')) {
                    event.preventDefault();
                    return;
                }

                if (maxDigits && /^\d$/.test(event.key)) {
                    const selectionStart = input.selectionStart ?? input.value.length;
                    const selectionEnd = input.selectionEnd ?? selectionStart;
                    const selectedDigits = input.value.slice(selectionStart, selectionEnd).replace(/\D/g, '').length;
                    const digitCount = input.value.replace(/\D/g, '').length - selectedDigits;
                    if (digitCount >= maxDigits) {
                        event.preventDefault();
                    }
                }
            });

            const enforce = (finalizeMinimum) => {
                if (!input.value) {
                    lastAcceptedValue = '';
                    input.setCustomValidity('');
                    return;
                }

                const sanitized = sanitizeNumericValue(input.value, integerOnly, maxDigits);
                if (!sanitized) {
                    input.value = '';
                    lastAcceptedValue = '';
                    input.setCustomValidity('');
                    return;
                }

                const value = Number(sanitized);
                if (!Number.isFinite(value)) {
                    input.value = lastAcceptedValue;
                    return;
                }

                let bounded = value;
                if (max !== null && bounded > max) bounded = max;
                if (finalizeMinimum && min !== null && bounded < min) bounded = min;
                if (integerOnly) bounded = Math.trunc(bounded);

                const next = String(bounded);
                if (input.value !== next) {
                    input.value = next;
                }
                lastAcceptedValue = next;
                input.setCustomValidity('');
            };

            input.addEventListener('input', () => enforce(false));
            input.addEventListener('paste', () => setTimeout(() => enforce(false), 0));
            input.addEventListener('drop', () => setTimeout(() => enforce(false), 0));
            input.addEventListener('blur', () => enforce(true));
            input.addEventListener('wheel', (event) => {
                if (document.activeElement === input) {
                    event.preventDefault();
                    input.blur();
                }
            }, { passive: false });
        });
    }

    function resolveMaxDigits(input, max) {
        const explicit = Number(input.dataset.maxDigits || input.dataset.numericMaxDigits || '');
        if (Number.isInteger(explicit) && explicit > 0) return explicit;

        if (max !== null && max >= 0 && Number.isFinite(max)) {
            return Math.max(1, Math.trunc(max).toString().length);
        }

        const name = `${input.name} ${input.id}`.toLowerCase();
        if (name.includes('fuel') || name.includes('percent')) return 3;
        if (name.includes('mileage') || name.includes('kilometer')) return 7;
        if (name.includes('year')) return 4;
        return null;
    }

    function sanitizeNumericValue(raw, integerOnly, maxDigits) {
        let value = String(raw ?? '').trim();
        if (integerOnly) {
            const negative = value.startsWith('-');
            value = value.replace(/\D/g, '');
            if (maxDigits) value = value.slice(0, maxDigits);
            return negative ? `-${value}` : value;
        }

        value = value.replace(',', '.').replace(/[^0-9.-]/g, '');
        const firstDot = value.indexOf('.');
        if (firstDot >= 0) {
            value = value.slice(0, firstDot + 1) + value.slice(firstDot + 1).replace(/\./g, '');
        }
        if (maxDigits) {
            const sign = value.startsWith('-') ? '-' : '';
            const unsigned = value.replace('-', '');
            const [whole, fraction = ''] = unsigned.split('.');
            const clippedWhole = whole.slice(0, maxDigits);
            value = `${sign}${clippedWhole}${unsigned.includes('.') ? `.${fraction}` : ''}`;
        }
        return value;
    }

    function normalizeInitialValue(raw, min, max, integerOnly, maxDigits) {
        if (!raw) return '';
        const sanitized = sanitizeNumericValue(raw, integerOnly, maxDigits);
        const value = Number(sanitized);
        if (!Number.isFinite(value)) return '';
        let bounded = value;
        if (max !== null && bounded > max) bounded = max;
        if (min !== null && bounded < min) bounded = min;
        if (integerOnly) bounded = Math.trunc(bounded);
        return String(bounded);
    }

    function parseFinite(value) {
        if (value === '') return null;
        const number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    async function initializeFileDrafts() {
        if (!('indexedDB' in window)) return;

        const db = await openDraftDb();
        await purgeExpiredDrafts(db);
        await clearServerConfirmedDrafts(db);

        const registerInput = async (input) => {
            if (!(input instanceof HTMLInputElement) || input.type !== 'file' || !input.dataset.fileDraftKey) return;
            if (input.dataset.fileDraftInitialized === 'true') return;
            input.dataset.fileDraftInitialized = 'true';
            input.addEventListener('change', () => saveInputDraft(db, input));
            await restoreInputDraft(db, input);
        };

        const registerTree = async (root) => {
            if (root instanceof HTMLInputElement) {
                await registerInput(root);
            }
            if (!(root instanceof Element) && root !== document) return;
            const inputs = Array.from(root.querySelectorAll?.('input[type="file"][data-file-draft-key]') || []);
            for (const input of inputs) {
                await registerInput(input);
            }
        };

        await registerTree(document);

        const observer = new MutationObserver((mutations) => {
            mutations.forEach((mutation) => {
                mutation.addedNodes.forEach((node) => {
                    if (node instanceof Element) {
                        registerTree(node).catch(() => {});
                    }
                });
            });
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    function openDraftDb() {
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(draftDbName, 1);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains(draftStoreName)) {
                    db.createObjectStore(draftStoreName, { keyPath: 'key' });
                }
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    function transactionRequest(db, mode, action) {
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(draftStoreName, mode);
            const store = transaction.objectStore(draftStoreName);
            const request = action(store);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    async function saveInputDraft(db, input) {
        const key = input.dataset.fileDraftKey;
        if (!key) return;

        const files = Array.from(input.files || []);
        if (files.length === 0) {
            await transactionRequest(db, 'readwrite', (store) => store.delete(key));
            return;
        }

        const payload = {
            key,
            storedAt: Date.now(),
            files: files.map((file) => ({
                name: file.name,
                type: file.type,
                lastModified: file.lastModified,
                blob: file
            }))
        };
        await transactionRequest(db, 'readwrite', (store) => store.put(payload));
    }

    async function restoreInputDraft(db, input) {
        const key = input.dataset.fileDraftKey;
        if (!key || (input.files && input.files.length > 0)) return;

        const payload = await transactionRequest(db, 'readonly', (store) => store.get(key));
        if (!payload || Date.now() - payload.storedAt > draftMaxAgeMs) return;

        const transfer = new DataTransfer();
        (payload.files || []).forEach((item) => {
            transfer.items.add(new File(
                [item.blob],
                item.name,
                { type: item.type, lastModified: item.lastModified }
            ));
        });

        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    async function clearServerConfirmedDrafts(db) {
        const marker = document.querySelector('[data-clear-file-draft-keys]');
        const raw = marker?.getAttribute('data-clear-file-draft-keys') || '';
        const keys = raw.split('|').map((value) => value.trim()).filter(Boolean);
        for (const key of new Set(keys)) {
            await transactionRequest(db, 'readwrite', (store) => store.delete(key));
        }
    }

    async function purgeExpiredDrafts(db) {
        const all = await transactionRequest(db, 'readonly', (store) => store.getAll());
        const expired = (all || []).filter((item) => Date.now() - item.storedAt > draftMaxAgeMs);
        for (const item of expired) {
            await transactionRequest(db, 'readwrite', (store) => store.delete(item.key));
        }
    }
})();
