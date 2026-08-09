(() => {
    const avatarInput = document.querySelector('[data-avatar-input]');
    const avatarSubmit = document.querySelector('[data-avatar-submit]');
    const avatarFileName = document.querySelector('[data-avatar-file-name]');

    if (avatarInput && avatarSubmit && avatarFileName) {
        avatarInput.addEventListener('change', () => {
            const file = avatarInput.files && avatarInput.files.length > 0
                ? avatarInput.files[0]
                : null;

            avatarSubmit.disabled = !file;
            avatarFileName.textContent = file
                ? file.name
                : 'JPG, PNG, WEBP · tối đa 2 MB';

            if (!file) return;

            const avatar = document.querySelector('.profile-header .profile-avatar');
            if (!avatar) return;

            const objectUrl = URL.createObjectURL(file);
            if (avatar.tagName === 'IMG') {
                avatar.src = objectUrl;
            } else {
                avatar.textContent = '';
                avatar.style.backgroundImage = `url("${objectUrl}")`;
                avatar.style.backgroundSize = 'cover';
                avatar.style.backgroundPosition = 'center';
            }
        });
    }

    document.querySelectorAll('[data-upload-input]').forEach(input => {
        input.addEventListener('change', () => {
            const file = input.files && input.files.length > 0 ? input.files[0] : null;
            const card = input.closest('[data-upload-card]');
            if (!card) return;

            const preview = card.querySelector('[data-upload-preview]');
            const placeholder = card.querySelector('[data-upload-placeholder]');
            const name = card.querySelector('[data-upload-name]');

            if (!file) {
                if (preview) {
                    preview.removeAttribute('src');
                    preview.classList.remove('is-visible');
                }
                if (placeholder) placeholder.classList.remove('d-none');
                if (name) name.textContent = 'Chọn ảnh';
                card.classList.remove('has-file');
                return;
            }

            const objectUrl = URL.createObjectURL(file);
            if (preview) {
                preview.src = objectUrl;
                preview.classList.add('is-visible');
            }
            if (placeholder) placeholder.classList.add('d-none');
            if (name) name.textContent = file.name;
            card.classList.add('has-file');
        });
    });

    function normalizeDateInput(input) {
        const digits = input.value.replace(/\D/g, '').slice(0, 8);
        const parts = [];
        if (digits.length > 0) parts.push(digits.slice(0, 2));
        if (digits.length > 2) parts.push(digits.slice(2, 4));
        if (digits.length > 4) parts.push(digits.slice(4, 8));
        input.value = parts.join('/');
    }

    function convertVietnameseDateToIso(value) {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value.trim());
        if (!match) return null;

        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);

        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) {
            return null;
        }

        return `${year.toString().padStart(4, '0')}-${month.toString().padStart(2, '0')}-${day.toString().padStart(2, '0')}`;
    }

    document.querySelectorAll('[data-vn-date]').forEach(input => {
        const sync = () => {
            const targetId = input.dataset.dateTarget;
            const target = targetId ? document.getElementById(targetId) : null;
            if (!target) return true;

            const iso = convertVietnameseDateToIso(input.value);
            target.value = iso || '';
            input.classList.toggle('is-invalid', input.value.trim().length > 0 && !iso);
            return Boolean(iso);
        };

        input.addEventListener('input', () => {
            normalizeDateInput(input);
            sync();
        });
        input.addEventListener('blur', sync);
    });

    const packageForm = document.querySelector('[data-kyc-package-form]');
    if (packageForm) {
        packageForm.addEventListener('submit', event => {
            let firstInvalidDate = null;

            packageForm.querySelectorAll('[data-vn-date]').forEach(input => {
                const targetId = input.dataset.dateTarget;
                const target = targetId ? document.getElementById(targetId) : null;
                const iso = convertVietnameseDateToIso(input.value);

                if (target) target.value = iso || '';
                input.classList.toggle('is-invalid', !iso);
                if (!iso && !firstInvalidDate) firstInvalidDate = input;
            });

            if (firstInvalidDate) {
                event.preventDefault();
                event.stopImmediatePropagation();
                firstInvalidDate.focus();
                firstInvalidDate.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        }, true);
    }

    document.querySelectorAll('[data-scroll-to]').forEach(button => {
        button.addEventListener('click', () => {
            const selector = button.dataset.scrollTo;
            const target = selector ? document.querySelector(selector) : null;
            target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
})();
