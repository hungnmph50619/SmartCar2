(() => {
    const modal = document.getElementById('documentPreviewModal');
    const previewImage = document.getElementById('documentPreviewImage');
    const previewTitle = document.getElementById('documentPreviewTitle');
    if (!modal || !previewImage) return;

    const open = trigger => {
        const src = trigger.dataset.previewSrc;
        if (!src) return;
        previewImage.src = src;
        if (previewTitle && trigger.dataset.previewTitle) {
            previewTitle.textContent = trigger.dataset.previewTitle;
        }
        modal.hidden = false;
        modal.setAttribute('aria-hidden', 'false');
        document.body.classList.add('doc-modal-open');
    };

    const close = () => {
        modal.hidden = true;
        modal.setAttribute('aria-hidden', 'true');
        previewImage.removeAttribute('src');
        document.body.classList.remove('doc-modal-open');
    };

    document.querySelectorAll('.doc-preview-trigger, .doc-inline-preview')
        .forEach(trigger => trigger.addEventListener('click', () => open(trigger)));

    modal.querySelectorAll('[data-preview-close]')
        .forEach(button => button.addEventListener('click', close));

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !modal.hidden) close();
    });
})();
