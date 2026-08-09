(() => {
    const modalElement = document.getElementById('kycImageModal');
    const imageElement = document.getElementById('kycImageModalImage');
    const titleElement = document.getElementById('kycImageModalTitle');

    if (!modalElement || !imageElement) return;

    let rotation = 0;
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

    const applyRotation = () => {
        imageElement.style.transform = `rotate(${rotation}deg)`;
    };

    document.querySelectorAll('[data-kyc-image]').forEach(button => {
        button.addEventListener('click', () => {
            const src = button.getAttribute('data-image-src');
            if (!src) return;

            imageElement.src = src;
            imageElement.alt = button.getAttribute('data-image-title') || 'Ảnh giấy tờ';
            if (titleElement) {
                titleElement.textContent = button.getAttribute('data-image-title') || 'Ảnh giấy tờ';
            }
            rotation = 0;
            applyRotation();
            modal.show();
        });
    });

    document.querySelector('[data-rotate-left]')?.addEventListener('click', () => {
        rotation -= 90;
        applyRotation();
    });

    document.querySelector('[data-rotate-right]')?.addEventListener('click', () => {
        rotation += 90;
        applyRotation();
    });

    document.querySelector('[data-rotate-reset]')?.addEventListener('click', () => {
        rotation = 0;
        applyRotation();
    });

    modalElement.addEventListener('hidden.bs.modal', () => {
        imageElement.removeAttribute('src');
        rotation = 0;
        applyRotation();
    });
})();
