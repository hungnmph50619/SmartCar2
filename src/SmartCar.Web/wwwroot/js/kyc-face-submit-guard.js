(() => {
    document.addEventListener('DOMContentLoaded', () => {
        const form = document.querySelector('[data-kyc-package-form]');
        if (!form) return;
        const face = form.querySelector('input[name="FaceCaptureSessionId"]');
        const submit = form.querySelector('[data-kyc-submit]');
        const hint = form.querySelector('[data-submit-hint]');
        if (!face || !submit) return;

        const update = () => {
            const standardRequired = Array.from(form.querySelectorAll('[required]'))
                .every(control => control.validity.valid);
            const faceReady = Boolean(face.value);
            const ready = standardRequired && faceReady;
            submit.disabled = !ready;
            if (hint) {
                hint.textContent = ready
                    ? 'Đã đủ 4 ảnh giấy tờ + ảnh mặt chụp trực tiếp. Có thể gửi hồ sơ.'
                    : faceReady
                        ? 'Ảnh mặt đã có. Vui lòng hoàn tất các trường/ảnh giấy tờ còn thiếu.'
                        : 'Vui lòng hoàn tất thông tin, đủ 4 ảnh giấy tờ và chụp ảnh mặt trực tiếp.';
                hint.classList.toggle('text-success', ready);
                hint.classList.toggle('text-muted', !ready);
            }
        };

        form.addEventListener('input', update);
        form.addEventListener('change', update);
        form.addEventListener('submit', event => {
            update();
            if (!face.value) {
                event.preventDefault();
                document.querySelector('[data-identity-capture-widget]')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        });
        window.setInterval(update, 300);
        update();
    });
})();
