(() => {
    const showStepTwo = panel => {
        panel.querySelectorAll('[data-license-step-pane]').forEach(pane => {
            pane.classList.toggle('d-none', Number(pane.dataset.licenseStepPane) !== 2);
        });
        panel.querySelectorAll('[data-license-step-indicator]').forEach(indicator => {
            const step = Number(indicator.dataset.licenseStepIndicator);
            indicator.classList.toggle('is-active', step === 2);
            indicator.classList.toggle('is-done', step < 2);
        });
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    document.addEventListener('click', event => {
        const manual = event.target.closest('[data-license-manual]');
        if (manual) {
            const panel = manual.closest('[data-ekyc-panel="license"]');
            if (panel) panel.dataset.gplxManualMode = 'true';
            return;
        }

        const next = event.target.closest('[data-license-ocr]');
        if (!next) return;
        const panel = next.closest('[data-ekyc-panel="license"]');
        if (!panel || panel.dataset.gplxManualMode !== 'true') return;

        event.preventDefault();
        event.stopImmediatePropagation();
        showStepTwo(panel);
    }, true);
})();