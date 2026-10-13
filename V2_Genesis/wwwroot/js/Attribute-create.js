document.addEventListener('DOMContentLoaded', function () {
        initialiseDeclarationDate();
        initialiseMixedUse();
        initialiseMaskedContacts();
        initialiseConfirmEmails();
        initialiseEvidenceUpload();
        initialiseNumericInputs();
        initialiseSignaturePad();
        initialiseAttributeDraftRecovery();
        initialiseSubmitValidation();
    });

    // ─────────────────────────────────────────────
    // Declaration date
    // ─────────────────────────────────────────────
    function initialiseDeclarationDate() {
        const declDate = document.getElementById('declDate');

        if (!declDate) return;

        declDate.value = new Date().toLocaleDateString('en-ZA', {
            day: '2-digit',
            month: 'long',
            year: 'numeric'
        });
    }

    // ─────────────────────────────────────────────
    // ─────────────────────────────────────────────
    // Multipurpose / Mixed-use dropdown
    // ─────────────────────────────────────────────
    function initialiseMixedUse() {
        const category = document.getElementById('valuationCategory');
        const mixedRow = document.getElementById('mixedUseRow');
        const mixedValue = document.getElementById('isMixedUseValue');
        const mixedSelect = document.getElementById('mixedUseSelect');

        if (!category || !mixedRow || !mixedValue) return;

        function updateMixedUse() {
            const selectedValue = (category.value || '').trim().toLowerCase();
            const isMulti = selectedValue === 'multipurpose';

            mixedRow.style.display = isMulti ? '' : 'none';
            mixedValue.value = isMulti ? 'true' : 'false';

            if (mixedSelect) {
                mixedSelect.disabled = !isMulti;
                mixedSelect.required = isMulti;

                if (!isMulti) {
                    mixedSelect.value = '';
                    mixedSelect.setCustomValidity('');
                }
            }
        }

        category.addEventListener('change', updateMixedUse);
        updateMixedUse();
    }

    // ─────────────────────────────────────────────
    // Privacy-masked contact fields
    // The real value lives in a hidden bound input. The display input
    // reveals it only while the client is editing, then masks on blur.
    // ─────────────────────────────────────────────
    function maskAttributeEmail(value) {
        value = (value || '').trim();
        if (!value) return '';

      const at = value.indexOf(String.fromCharCode(64));
        if (at <= 0) return '***';

        const local = value.substring(0, at);
        const domain = value.substring(at);
        const visible = local.substring(0, Math.min(2, local.length));
        return visible + '*'.repeat(Math.max(3, local.length - visible.length)) + domain;
    }

    function maskAttributePhone(value) {
        value = (value || '').trim();
        if (!value) return '';
        if (value.length <= 4) return '*'.repeat(value.length);

        return value.substring(0, 1)
            + '*'.repeat(Math.max(1, value.length - 4))
            + value.substring(value.length - 3);
    }

    // SA numbers: 0XXXXXXXXX (any number starting with 0) or +27XXXXXXXXX.
    // Same rule as Helpers/PhoneNumberRules.cs on the server.
    function isValidAttributePhone(value) {
        let v = (value || '').trim().replace(/[\s\-().]/g, '');
        if (!v) return false;
        if (v.indexOf('+27') === 0) v = v.substring(3);
        else if (v.indexOf('27') === 0 && v.length === 11) v = v.substring(2);
        else if (v.charAt(0) === '0') return /^0\d{9}$/.test(v);
        else return false;
        if (v.charAt(0) === '0') v = v.substring(1);
        return /^[1-9]\d{8}$/.test(v);
    }

    function validateAttributeCellNumbers(showRequired) {
        let firstBad = null;
        document.querySelectorAll('.masked-contact-display[data-mask-kind="phone"]').forEach(function (display) {
            const index = display.dataset.index;
            const real = document.querySelector('.masked-contact-real[data-mask-kind="phone"][data-index="' + index + '"]');
            const hint = document.getElementById('cellHint-' + index);
            const value = real ? real.value : '';
            let message = '';

            if (!value) {
                if (showRequired) message = 'Cell number is required.';
            } else if (!isValidAttributePhone(value)) {
                message = 'Enter a number starting with 0 (e.g. 0671234567) or +27 (e.g. +27671234567).';
            }

            display.classList.toggle('is-invalid', !!message);
            display.style.borderColor = message ? '#dc3545' : '';
            if (hint) {
                hint.textContent = message ||
                    'Starts with 0 (e.g. 067 123 4567) or +27 (e.g. +27 67 123 4567).';
                hint.style.color = message ? '#dc3545' : '#5b6b70';
            }
            if (message && !firstBad) firstBad = display;
        });
        return firstBad;
    }

    function initialiseMaskedContacts() {
        document.querySelectorAll('.masked-contact-display').forEach(function (display) {
            const kind = display.dataset.maskKind;
            const index = display.dataset.index;
            const real = document.querySelector(
                '.masked-contact-real[data-mask-kind="' + kind + '"][data-index="' + index + '"]'
            );

            if (!real) return;

            const applyMask = function () {
                display.value = kind === 'email'
                    ? maskAttributeEmail(real.value)
                    : maskAttributePhone(real.value);
            };

            display.addEventListener('focus', function () {
                display.value = real.value || '';
            });

            display.addEventListener('input', function () {
                real.value = display.value;
            });

            display.addEventListener('blur', function () {
                real.value = display.value.trim();
                applyMask();
                if (kind === 'phone') validateAttributeCellNumbers(false);
            });

            applyMask();
        });

        const form = document.getElementById('attributeCreateForm');
        form?.addEventListener('submit', function () {
            document.querySelectorAll('.masked-contact-display').forEach(function (display) {
                const kind = display.dataset.maskKind;
                const index = display.dataset.index;
                const real = document.querySelector(
                    '.masked-contact-real[data-mask-kind="' + kind + '"][data-index="' + index + '"]'
                );

                // Copy what is in the box when it is not the masked text
                // (focused, or filled by browser autofill without an input event).
                if (real && (document.activeElement === display ||
                             (display.value && display.value.indexOf('*') === -1))) {
                    real.value = display.value.trim();
                }
            });
        });
    }


    // ─────────────────────────────────────────────
    // Confirm email
    // ─────────────────────────────────────────────
    function normaliseAttributeEmail(value) {
        return (value || '').trim().toLowerCase();
    }

    function isValidAttributeEmail(value) {
        return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value || '');
    }

    function validateAttributeEmailPair(index, showRequiredMessage) {
        const primary =
            document.querySelector('.attr-email-primary[data-email-index="' + index + '"]');

        const confirm =
            document.querySelector('.attr-email-confirm[data-email-index="' + index + '"]');

        const real =
            document.querySelector(
                '.masked-contact-real[data-mask-kind="email"][data-index="' + index + '"]'
            );

        const status =
            document.getElementById('confirmEmailStatus-' + index);

        if (!primary || !confirm || !real) return true;

        const email =
            normaliseAttributeEmail(real.value || primary.value);

        const confirmation =
            normaliseAttributeEmail(confirm.value);

        primary.classList.remove('is-valid-email', 'is-invalid-email');
        confirm.classList.remove('is-valid-email', 'is-invalid-email');

        if (status) {
            status.classList.remove('valid', 'invalid');
            status.textContent = '';
        }

        if (!email || !isValidAttributeEmail(email)) {
            primary.classList.add('is-invalid-email');
            confirm.setCustomValidity('Please enter a valid email address first.');

            if (status) {
                status.classList.add('invalid');
                status.textContent = 'Please enter a valid email address.';
            }

            return false;
        }

        if (!confirmation) {
            confirm.setCustomValidity(
                showRequiredMessage ? 'Please confirm the email address.' : ''
            );

            if (showRequiredMessage) {
                confirm.classList.add('is-invalid-email');

                if (status) {
                    status.classList.add('invalid');
                    status.textContent = 'Please confirm the email address.';
                }

                return false;
            }

            return true;
        }

        if (email !== confirmation) {
            primary.classList.add('is-invalid-email');
            confirm.classList.add('is-invalid-email');
            confirm.setCustomValidity('Email addresses do not match.');

            if (status) {
                status.classList.add('invalid');
                status.textContent = 'Email addresses do not match.';
            }

            return false;
        }

        primary.classList.add('is-valid-email');
        confirm.classList.add('is-valid-email');
        confirm.setCustomValidity('');

        if (status) {
            status.classList.add('valid');
            status.textContent = '✓ Email addresses match';
        }

        return true;
    }

    function validateAllAttributeEmailPairs(showRequiredMessage) {
        let valid = true;

        document.querySelectorAll('.attr-email-confirm').forEach(function (confirm) {
            const index = confirm.dataset.emailIndex;

            if (!validateAttributeEmailPair(index, showRequiredMessage)) {
                valid = false;
            }
        });

        return valid;
    }

    function initialiseConfirmEmails() {
        document.querySelectorAll('.attr-email-primary').forEach(function (primary) {
            const index = primary.dataset.emailIndex;

            primary.addEventListener('input', function () {
                const real =
                    document.querySelector(
                        '.masked-contact-real[data-mask-kind="email"][data-index="' + index + '"]'
                    );

                if (real) real.value = primary.value;

                validateAttributeEmailPair(index, false);
                scheduleAttributeDraftSave();
            });

            primary.addEventListener('blur', function () {
                validateAttributeEmailPair(index, false);
            });
        });

        document.querySelectorAll('.attr-email-confirm').forEach(function (confirm) {
            const index = confirm.dataset.emailIndex;

            confirm.addEventListener('input', function () {
                validateAttributeEmailPair(index, false);
                scheduleAttributeDraftSave();
            });

            confirm.addEventListener('blur', function () {
                validateAttributeEmailPair(index, true);
            });
        });
    }

    // ─────────────────────────────────────────────
    // Owner / Company contact toggle
    // Uses event delegation on document so it keeps
    // working even if rows are re-rendered later.
    // ─────────────────────────────────────────────
    function initialiseContactTypeToggle() {
        document.addEventListener('change', function (e) {
            if (!e.target.classList.contains('contact-type-radio')) return;
            updateContactType(e.target.dataset.index);
        });

        document.querySelectorAll('.contact-table').forEach(function (table) {
            updateContactType(table.dataset.contactIndex);
        });
    }

    function updateContactType(index) {
        if (index === undefined || index === null || index === '') return;

        const selected = document.querySelector(
            'input[name="ContactInfos[' + index + '].IsCompany"]:checked'
        );

        const isCompany = selected ? selected.value === 'true' : false;

        const ownerRows = document.querySelectorAll('.owner-fields-' + index);
        const companyRows = document.querySelectorAll('.company-fields-' + index);
        const heading = document.getElementById('contactHeading-' + index);

        const hiddenContactType = document.querySelector(
            'input[name="ContactInfos[' + index + '].ContactType"]'
        );

        ownerRows.forEach(function (row) {
            row.style.display = isCompany ? 'none' : '';
        });

        companyRows.forEach(function (row) {
            row.style.display = isCompany ? '' : 'none';
        });

        if (heading) {
            heading.textContent = isCompany ? 'Company Details' : 'Individual Details';
        }

        if (hiddenContactType) {
            hiddenContactType.value = isCompany ? 'Company' : 'Owner';
        }

        // Toggle required-ness without clearing either set of values.
        // A client may switch between Owner and Company while completing
        // the form, so all captured names must be preserved.
        const firstNames = document.querySelector('[name="ContactInfos[' + index + '].FirstNames"]');
        const surname = document.querySelector('[name="ContactInfos[' + index + '].LastName"]');
        const companyName = document.querySelector('[name="ContactInfos[' + index + '].CompanyName"]');
        const companyReg = document.querySelector('[name="ContactInfos[' + index + '].CompanyRegistrationNumber"]');

        if (firstNames) firstNames.required = !isCompany;
        if (surname) surname.required = !isCompany;

        if (companyName) {
            if (isCompany && !companyName.value.trim()) {
                const combinedName = [
                    surname ? surname.value.trim() : '',
                    firstNames ? firstNames.value.trim() : ''
                ].filter(Boolean).join(' ');

                companyName.value = combinedName;
            }

            companyName.required = isCompany;
        }

        // Company registration and personal names are intentionally
        // retained when the contact type changes.
        if (companyReg) companyReg.required = false;
    }

    // ─────────────────────────────────────────────
    // "Postal address is the same as physical address"
    // Event delegation, keyed per contact index so it
    // works independently for each Owner/Company block.
    // ─────────────────────────────────────────────
    function initialiseSameAddressToggle() {
        document.addEventListener('change', function (e) {
            if (!e.target.classList.contains('same-postal-checkbox')) return;
            applySameAddress(e.target.dataset.index, e.target.checked);
        });

        document.addEventListener('input', function (e) {
            if (!e.target.matches('.attr-textarea')) return;
            if (e.target.id.indexOf('physicalAddress-') !== 0) return;

            const index = e.target.id.replace('physicalAddress-', '');
            const checkbox = document.getElementById('samePostalAddress-' + index);

            if (checkbox && checkbox.checked) {
                applySameAddress(index, true);
            }
        });

        document.querySelectorAll('.same-postal-checkbox').forEach(function (checkbox) {
            if (checkbox.checked) {
                applySameAddress(checkbox.dataset.index, true);
            }
        });
    }

    function applySameAddress(index, isSame) {
        const physical = document.getElementById('physicalAddress-' + index);
        const postal = document.getElementById('postalAddress-' + index);

        if (!physical || !postal) return;

        if (isSame) {
            postal.value = physical.value;
            postal.readOnly = true;
            postal.classList.add('attr-textarea-synced');
        } else {
            postal.readOnly = false;
            postal.classList.remove('attr-textarea-synced');
        }
    }

    // ─────────────────────────────────────────────
    // Dynamic rows
    // ─────────────────────────────────────────────
    function delRow(btn) {
        const row = btn.closest('tr');

        if (!row) return;

        const tbody = row.closest('tbody');

        row.remove();

        if (tbody && tbody.rows.length === 0) {
            // Leave empty; user can add again using button.
        }

        reIndex();
    }

    function reIndex() {
        const bodyIds = [
            'body-bus-buildings',
            'body-bus-sections',
            'body-drc-buildings',
            'body-drc-improvements',
            'body-drc-vacant'
        ];

        bodyIds.forEach(function (bodyId) {
            const tbody = document.getElementById(bodyId);

            if (!tbody) return;

            Array.from(tbody.rows).forEach(function (row, i) {
                row.querySelectorAll('[name]').forEach(function (el) {
                    el.name = el.name.replace(/\[\d+\]/, '[' + i + ']');
                });
            });
        });
    }

    function addRow(bodyId, templateFn) {
        const tbody = document.getElementById(bodyId);

        if (!tbody || typeof templateFn !== 'function') return;

        const idx = tbody.rows.length;
        const tr = document.createElement('tr');

        tr.innerHTML = templateFn(idx);
        tbody.appendChild(tr);

        reIndex();
    }

    function qualitySelect(name) {
        return '<select name="' + name + '" class="attr-input attr-select">' +
            '<option value="">-- Select --</option>' +
            '<option>Excellent</option>' +
            '<option>Good</option>' +
            '<option>Average</option>' +
            '<option>Fair</option>' +
            '<option>Poor</option>' +
            '</select>';
    }

    function condSelect(name) {
        return qualitySelect(name);
    }

    function delBtn() {
        return '<td>' +
            '<button type="button" class="dyn-del-btn" onclick="delRow(this)" title="Remove">' +
            '<i class="fa-solid fa-xmark"></i>' +
            '</button>' +
            '</td>';
    }

    const busBuildingTemplate = function (i) {
        return '' +
            '<td><input name="BusinessBuildings[' + i + '].BuildingNr" class="attr-input" /></td>' +
            '<td>' + qualitySelect('BusinessBuildings[' + i + '].Quality') + '</td>' +
            '<td>' + condSelect('BusinessBuildings[' + i + '].Condition') + '</td>' +
            '<td><input name="BusinessBuildings[' + i + '].YearBuilt" class="attr-input" type="number" /></td>' +
            '<td><input name="BusinessBuildings[' + i + '].Storeys" class="attr-input" type="number" /></td>' +
            '<td><input name="BusinessBuildings[' + i + '].GBA" class="attr-input" type="number" step="0.01" /></td>' +
            '<input type="hidden" name="BusinessBuildings[' + i + '].Depreciation" />' +
            '<input type="hidden" name="BusinessBuildings[' + i + '].Cost" />' +
            '<input type="hidden" name="BusinessBuildings[' + i + '].DRC" />' +
            delBtn();
    };

    const busSectionTemplate = function (i) {
        return '' +
            '<td><input name="BusinessSections[' + i + '].BuildingNr" class="attr-input" /></td>' +
            '<td><input name="BusinessSections[' + i + '].Usage" class="attr-input" /></td>' +
            '<td><input name="BusinessSections[' + i + '].GBA" class="attr-input" type="number" step="0.01" /></td>' +
            '<td><input name="BusinessSections[' + i + '].NLA" class="attr-input" type="number" step="0.01" /></td>' +
            '<td><input name="BusinessSections[' + i + '].Rental" class="attr-input" type="number" step="0.01" /></td>' +
            '<input type="hidden" name="BusinessSections[' + i + '].MarketGroup" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Quality" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].CostRate" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Cost" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Vac" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Exp" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Cap" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Gross" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Normalised" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Nett" />' +
            '<input type="hidden" name="BusinessSections[' + i + '].Value" />' +
            delBtn();
    };

    const drcBuildingTemplate = function (i) {
        return '' +
            '<td><input name="DrcBuildings[' + i + '].BuildingDescription" class="attr-input" /></td>' +
            '<td>' + qualitySelect('DrcBuildings[' + i + '].Quality') + '</td>' +
            '<td><input name="DrcBuildings[' + i + '].GrossBuildingArea" class="attr-input" type="number" step="0.01" /></td>' +
            '<td>' + condSelect('DrcBuildings[' + i + '].Condition') + '</td>' +
            '<input type="hidden" name="DrcBuildings[' + i + '].DepreciationPercentage" />' +
            '<input type="hidden" name="DrcBuildings[' + i + '].RatePerSQM" />' +
            '<input type="hidden" name="DrcBuildings[' + i + '].DepreciatedRate" />' +
            '<input type="hidden" name="DrcBuildings[' + i + '].ReplacementCost" />' +
            delBtn();
    };

    const drcImprovementTemplate = function (i) {
        return '' +
            '<td><input name="DrcImprovements[' + i + '].ImprovementDescription" class="attr-input" /></td>' +
            '<td>' + qualitySelect('DrcImprovements[' + i + '].Quality') + '</td>' +
            '<td><input name="DrcImprovements[' + i + '].AreaUnit" class="attr-input" type="number" step="0.01" /></td>' +
            '<td>' + condSelect('DrcImprovements[' + i + '].Condition') + '</td>' +
            '<input type="hidden" name="DrcImprovements[' + i + '].DepreciationPercentage" />' +
            '<input type="hidden" name="DrcImprovements[' + i + '].RatePerSQM" />' +
            '<input type="hidden" name="DrcImprovements[' + i + '].DepreciatedRate" />' +
            '<input type="hidden" name="DrcImprovements[' + i + '].ReplacementCost" />' +
            delBtn();
    };

    const drcVacantTemplate = function (i) {
        return '' +
            '<td><input name="DrcVacantLands[' + i + '].Region" class="attr-input" /></td>' +
            '<td><input name="DrcVacantLands[' + i + '].Area" class="attr-input" type="number" step="0.01" /></td>' +
            '<input type="hidden" name="DrcVacantLands[' + i + '].MinRatePerSQM" />' +
            '<input type="hidden" name="DrcVacantLands[' + i + '].MidRatePerSQM" />' +
            '<input type="hidden" name="DrcVacantLands[' + i + '].MaxRatePerSQM" />' +
            '<input type="hidden" name="DrcVacantLands[' + i + '].Rate" />' +
            '<input type="hidden" name="DrcVacantLands[' + i + '].VacantLandCost" />' +
            delBtn();
    };

    // ─────────────────────────────────────────────
    // Evidence upload counter/list
    // ─────────────────────────────────────────────
    // ─────────────────────────────────────────────
    // Numeric field protection
    // - no copy/paste into numeric fields
    // - no alphabetic/symbol characters
    // - integer/decimal rules follow each input's step
    // ─────────────────────────────────────────────
    function initialiseNumericInputs() {
        const form = document.getElementById('attributeCreateForm');
        if (!form) return;

        function prepareNumericInput(input) {
            if (!input || input.dataset.numericGuard === 'true') return;

            input.dataset.numericGuard = 'true';
            input.setAttribute('autocomplete', 'off');

            const step = (input.getAttribute('step') || '1').toLowerCase();
            const allowsDecimal =
                step === 'any' ||
                (step !== '' && step !== '1' && Number(step) % 1 !== 0);

            input.setAttribute('inputmode', allowsDecimal ? 'decimal' : 'numeric');

            input.addEventListener('paste', function (e) {
                e.preventDefault();
            });

            input.addEventListener('drop', function (e) {
                e.preventDefault();
            });

            input.addEventListener('keydown', function (e) {
                const allowedControlKeys = [
                    'Backspace', 'Delete', 'Tab',
                    'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown',
                    'Home', 'End'
                ];

                if (allowedControlKeys.includes(e.key)) return;

                if ((e.ctrlKey || e.metaKey) &&
                    ['a', 'c', 'x'].includes(e.key.toLowerCase())) {
                    return;
                }

                if (/^\d$/.test(e.key)) return;

                if (allowsDecimal &&
                    (e.key === '.' || e.key === ',') &&
                    !input.value.includes('.') &&
                    !input.value.includes(',')) {
                    return;
                }

                e.preventDefault();
            });

            input.addEventListener('input', function () {
                let value = input.value || '';

                if (allowsDecimal) {
                    value = value.replace(',', '.');
                    value = value.replace(/[^\d.]/g, '');

                    const firstDot = value.indexOf('.');
                    if (firstDot >= 0) {
                        value =
                            value.substring(0, firstDot + 1) +
                            value.substring(firstDot + 1).replace(/\./g, '');
                    }
                } else {
                    value = value.replace(/\D/g, '');
                }

                if (input.value !== value) {
                    input.value = value;
                }
            });
        }

        form.querySelectorAll('input[type="number"]').forEach(prepareNumericInput);

        // Dynamic rows are added later. Guard newly-added numeric inputs too.
        const observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (!(node instanceof HTMLElement)) return;

                    if (node.matches && node.matches('input[type="number"]')) {
                        prepareNumericInput(node);
                    }

                    node.querySelectorAll?.('input[type="number"]')
                        .forEach(prepareNumericInput);
                });
            });
        });

        observer.observe(form, { childList: true, subtree: true });
    }

    let selectedEvidenceFiles = new DataTransfer();
    let selectedEvidenceTypes = [];

    function currentEvidenceType() {
        const select = document.getElementById('evEvidenceType');
        return select ? (select.value || '').trim() : '';
    }

    function requireEvidenceType() {
        const select = document.getElementById('evEvidenceType');

        if (select && select.value) {
            select.setCustomValidity('');
            return select.value;
        }

        if (select) {
            select.setCustomValidity('Please select the evidence type before adding files.');
            select.reportValidity();
            select.focus();
        }

        return '';
    }

    function appendEvidenceFiles(files, evidenceType) {
        const maxFiles = 10;
        const maxFileSize = 20 * 1024 * 1024;

        // Exact file types requested for client evidence.
        const allowedExtensions = ['pdf', 'xls', 'xlsx', 'jpg', 'jpeg', 'png'];

        const existing = new Set(
            Array.from(selectedEvidenceFiles.files).map(fileKey)
        );

        const rejected = [];

        Array.from(files || []).forEach(function (file) {
            const ext = (file.name.split('.').pop() || '').toLowerCase();
            const key = fileKey(file);

            if (existing.has(key)) {
                rejected.push(file.name + ' has already been added.');
                return;
            }

            if (!allowedExtensions.includes(ext)) {
                rejected.push(
                    file.name +
                    ' is not allowed. Upload PDF, Excel, JPG, JPEG or PNG files only.'
                );
                return;
            }

            if (file.size > maxFileSize) {
                rejected.push(file.name + ' is larger than 20 MB.');
                return;
            }

            if (selectedEvidenceFiles.files.length >= maxFiles) {
                rejected.push('Only 10 supporting documents can be selected.');
                return;
            }

            selectedEvidenceFiles.items.add(file);
            selectedEvidenceTypes.push(evidenceType);
            existing.add(key);
        });

        if (rejected.length) {
            alert(Array.from(new Set(rejected)).join('\n'));
        }
    }

    function fileKey(file) {
        return [file.name, file.size, file.lastModified].join('|');
    }

    function syncEvidenceTypeInputs() {
        const holder = document.getElementById('evEvidenceTypesHolder');
        if (!holder) return;

        holder.innerHTML = '';

        selectedEvidenceTypes.forEach(function (type) {
            const hidden = document.createElement('input');
            hidden.type = 'hidden';
            hidden.name = 'EvidenceTypes';
            hidden.value = type;
            holder.appendChild(hidden);
        });

        // Keep files and classifications safe through refresh.
        saveAttributeEvidenceDraft();
    }

    function removeEvidenceFile(index) {
        const replacement = new DataTransfer();
        const replacementTypes = [];

        Array.from(selectedEvidenceFiles.files).forEach(function (file, currentIndex) {
            if (currentIndex !== index) {
                replacement.items.add(file);
                replacementTypes.push(selectedEvidenceTypes[currentIndex] || 'Supporting Document');
            }
        });

        selectedEvidenceFiles = replacement;
        selectedEvidenceTypes = replacementTypes;

        const input = document.getElementById('evFileInput');
        if (input) input.files = selectedEvidenceFiles.files;

        syncEvidenceTypeInputs();

        updateEvidenceList(
            selectedEvidenceFiles.files,
            document.getElementById('evFileList'),
            document.getElementById('evCountText'),
            document.getElementById('evCountFill')
        );
    }

    function initialiseEvidenceUpload() {
        const input = document.getElementById('evFileInput');
        const list = document.getElementById('evFileList');
        const countText = document.getElementById('evCountText');
        const countFill = document.getElementById('evCountFill');
        const dropzone = document.getElementById('evDropzone');
        const browse = document.getElementById('evBrowseFiles');
        const evidenceType = document.getElementById('evEvidenceType');

        initialiseRepLetterUpload();

        if (!input) return;

        evidenceType?.addEventListener('change', function () {
            evidenceType.setCustomValidity('');
        });

        function openFileBrowser() {
            if (!requireEvidenceType()) return;
            input.click();
        }

        browse?.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openFileBrowser();
        });

        dropzone?.addEventListener('click', function () {
            openFileBrowser();
        });

        dropzone?.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                openFileBrowser();
            }
        });

        input.addEventListener('change', function () {
            const type = requireEvidenceType();

            if (!type) {
                input.value = '';
                return;
            }

            appendEvidenceFiles(input.files, type);
            input.files = selectedEvidenceFiles.files;

            syncEvidenceTypeInputs();
            updateEvidenceList(
                selectedEvidenceFiles.files,
                list,
                countText,
                countFill
            );
        });

        if (dropzone) {
            dropzone.addEventListener('dragover', function (e) {
                e.preventDefault();
                dropzone.classList.add('drag-over');
            });

            dropzone.addEventListener('dragleave', function () {
                dropzone.classList.remove('drag-over');
            });

            dropzone.addEventListener('drop', function (e) {
                e.preventDefault();
                dropzone.classList.remove('drag-over');

                const type = requireEvidenceType();
                if (!type) return;

                if (e.dataTransfer && e.dataTransfer.files) {
                    appendEvidenceFiles(e.dataTransfer.files, type);
                    input.files = selectedEvidenceFiles.files;

                    syncEvidenceTypeInputs();
                    updateEvidenceList(
                        selectedEvidenceFiles.files,
                        list,
                        countText,
                        countFill
                    );
                }
            });
        }

        syncEvidenceTypeInputs();
        updateEvSummaryBadge();
    }

    // ─────────────────────────────────────────────
    // Authorisation letter — only present in the DOM
    // when the client is submitting as a representative.
    // ─────────────────────────────────────────────
    function initialiseRepLetterUpload() {
        const repInput = document.getElementById('evRepLetterInput');
        const repStatus = document.getElementById('evRepFileStatus');

        if (!repInput) return;

        repInput.addEventListener('change', function () {
            const file = repInput.files && repInput.files[0];

            if (repStatus) {
                if (file) {
                    const sizeMb = (file.size / 1024 / 1024).toFixed(2);
                    repStatus.classList.add('ev-rep-file-status--ok');
                    repStatus.innerHTML =
                        '<i class="fa-solid fa-circle-check"></i> ' +
                        escapeHtml(file.name) + ' <small>(' + sizeMb + ' MB)</small>';
                } else {
                    repStatus.classList.remove('ev-rep-file-status--ok');
                    repStatus.innerHTML =
                        '<i class="fa-solid fa-circle-exclamation"></i> No file selected yet';
                }
            }

            updateEvSummaryBadge();
        });
    }

    // ─────────────────────────────────────────────
    // Combined "files ready" summary badge
    // (authorisation letter + supporting documents)
    // ─────────────────────────────────────────────
    function updateEvSummaryBadge() {
        const badge = document.getElementById('evSummaryBadge');

        if (!badge) return;

        const evInput = document.getElementById('evFileInput');
        const repInput = document.getElementById('evRepLetterInput');

        const evCount = evInput && evInput.files ? evInput.files.length : 0;
        const repCount = repInput && repInput.files && repInput.files.length ? 1 : 0;
        const total = evCount + repCount;

        badge.textContent = total === 1 ? '1 file ready' : total + ' files ready';
        badge.classList.toggle('ev-summary-badge--filled', total > 0);
    }

           function updateEvidenceList(files, list, countText, countFill) {
        const maxFiles = 10;
        const maxFileSizeMb = 20;
        const allowedExtensions = ['pdf', 'xls', 'xlsx', 'jpg', 'jpeg', 'png'];
        const fileCount = files ? files.length : 0;

        let hasError = false;
        let errorMessages = [];

        if (fileCount > maxFiles) {
            hasError = true;
            errorMessages.push('You can upload a maximum of 10 files.');
        }

        Array.from(files || []).forEach(function (file) {
            const sizeMb = file.size / 1024 / 1024;
            const ext = (file.name.split('.').pop() || '').toLowerCase();

            if (!allowedExtensions.includes(ext)) {
                hasError = true;
                errorMessages.push(file.name + ' is not allowed. Upload PDF, Excel, JPG, JPEG or PNG files only.');
            }

            if (sizeMb > maxFileSizeMb) {
                hasError = true;
                errorMessages.push(file.name + ' is larger than 20 MB.');
            }
        });

        if (countText) {
            countText.textContent = fileCount + ' of ' + maxFiles + ' files added';
        }

        if (countFill) {
            const percent = Math.min((fileCount / maxFiles) * 100, 100);
            countFill.style.width = percent + '%';
            countFill.classList.toggle('danger', hasError);
        }

        updateEvSummaryBadge();

        if (!list) return;

        list.innerHTML = '';

        if (hasError) {
            const errorBox = document.createElement('div');
            errorBox.className = 'ev-file-error';
            errorBox.innerHTML =
                '<strong>File validation failed:</strong><br>' +
                errorMessages.map(escapeHtml).join('<br>');
            list.appendChild(errorBox);
        }

        if (!files || fileCount === 0) return;

        Array.from(files).forEach(function (file, index) {
            const item = document.createElement('div');
            const sizeMb = file.size / 1024 / 1024;
            const ext = (file.name.split('.').pop() || '').toLowerCase();
            const invalid =
                sizeMb > maxFileSizeMb ||
                !allowedExtensions.includes(ext) ||
                fileCount > maxFiles;

            item.className = invalid ? 'ev-file-item ev-file-item-invalid' : 'ev-file-item';

            const evidenceType =
                selectedEvidenceTypes[index] || 'Supporting Document';

            item.innerHTML =
                '<i class="fa-solid fa-file"></i>' +
                '<span class="ev-type-badge">' + escapeHtml(evidenceType) + '</span>' +
                '<span class="ev-file-name">' + escapeHtml(file.name) + '</span>' +
                '<small>' + sizeMb.toFixed(2) + ' MB</small>' +
                '<button type="button" class="ev-file-remove" title="Remove file" ' +
                'onclick="removeEvidenceFile(' + index + ')">' +
                '<i class="fa-solid fa-xmark"></i></button>';

            list.appendChild(item);
        });
    }

    function escapeHtml(value) {
        return String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    // ─────────────────────────────────────────────
    // ─────────────────────────────────────────────
    // Refresh-safe Attribute Create draft
    // ─────────────────────────────────────────────
    //
    // Normal fields: sessionStorage
    // Uploaded files: IndexedDB
    //
    // Browsers clear file inputs during refresh for security. IndexedDB
    // lets us safely reconstruct the user's selected files in the same
    // browser. Drafts expire after 24 hours.
    //
    const ATTRIBUTE_DRAFT_PREFIX = 'Genesis:AttributeCreate:v2:';
    const ATTRIBUTE_DRAFT_TTL_MS = 24 * 60 * 60 * 1000;
    const ATTRIBUTE_FILE_DB = 'GenesisAttributeCreateDrafts';
    const ATTRIBUTE_FILE_STORE = 'files';

    function getAttributeDraftKey() {
        const formType =
            document.querySelector('[name="FormType"]')?.value ||
            '@Model.FormType' ||
            'Unknown';

        const attrId =
            document.querySelector('[name="AttrId"]')?.value || '';

        const premiseId =
            document.querySelector('[name="PropertyDetails.PremiseId"]')?.value || '';

        const propertyDesc =
            document.querySelector('[name="PropertyDetails.PropertyDesc"]')?.value || '';

        return ATTRIBUTE_DRAFT_PREFIX +
            [formType, attrId || premiseId || propertyDesc || 'New']
                .join(':')
                .replace(/\s+/g, '_');
    }

    function getAttributeDraftStorageKey() {
        return getAttributeDraftKey() + ':form';
    }

    function getAttributeEvidenceStorageKey() {
        return getAttributeDraftKey() + ':evidence';
    }

    function getAttributeRepLetterStorageKey() {
        return getAttributeDraftKey() + ':rep-letter';
    }

    function openAttributeDraftDb() {
        return new Promise(function (resolve, reject) {
            if (!window.indexedDB) {
                reject(new Error('IndexedDB is not available.'));
                return;
            }

            const request = indexedDB.open(ATTRIBUTE_FILE_DB, 1);

            request.onupgradeneeded = function () {
                const db = request.result;

                if (!db.objectStoreNames.contains(ATTRIBUTE_FILE_STORE)) {
                    db.createObjectStore(
                        ATTRIBUTE_FILE_STORE,
                        { keyPath: 'key' }
                    );
                }
            };

            request.onsuccess = function () { resolve(request.result); };
            request.onerror = function () { reject(request.error); };
        });
    }

    function waitForAttributeTransaction(tx) {
        return new Promise(function (resolve, reject) {
            tx.oncomplete = function () { resolve(); };
            tx.onerror = function () { reject(tx.error); };
            tx.onabort = function () { reject(tx.error); };
        });
    }

    function getAttributeDraftRecord(store, key) {
        return new Promise(function (resolve, reject) {
            const request = store.get(key);
            request.onsuccess = function () { resolve(request.result || null); };
            request.onerror = function () { reject(request.error); };
        });
    }

    function collectAttributeDynamicRowCounts() {
        const ids = [
            'body-bus-buildings',
            'body-bus-sections',
            'body-drc-buildings',
            'body-drc-improvements',
            'body-drc-vacant'
        ];

        const result = {};

        ids.forEach(function (id) {
            const body = document.getElementById(id);
            if (body) result[id] = body.rows.length;
        });

        return result;
    }

    function collectAttributeFormDraft() {
        const form = document.getElementById('attributeCreateForm');
        if (!form) return null;

        const values = {};
        const idValues = {};
        const groups = new Map();

        form.querySelectorAll('input, select, textarea').forEach(function (el) {
            if (el.type === 'file' || el.type === 'password') return;
            if (el.name === '__RequestVerificationToken') return;

            if (el.name) {
                if (!groups.has(el.name)) groups.set(el.name, []);
                groups.get(el.name).push(el);
                return;
            }

            if (!el.id || el.type === 'button' || el.type === 'submit') return;

            idValues[el.id] =
                el.type === 'checkbox'
                    ? { kind: 'checkbox', checked: el.checked }
                    : { kind: 'value', value: el.value };
        });

        groups.forEach(function (controls, name) {
            const radios = controls.filter(x => x.type === 'radio');
            const checkbox = controls.find(x => x.type === 'checkbox');

            if (radios.length) {
                const selected = radios.find(x => x.checked);

                values[name] = {
                    kind: 'radio',
                    value: selected ? selected.value : null
                };
                return;
            }

            if (checkbox) {
                values[name] = {
                    kind: 'checkbox',
                    checked: checkbox.checked
                };
                return;
            }

            const control =
                controls.find(x => x.type !== 'hidden') ||
                controls[0];

            if (!control) return;

            values[name] = {
                kind: 'value',
                value: control.value
            };
        });

        return {
            version: 2,
            savedAt: Date.now(),
            rowCounts: collectAttributeDynamicRowCounts(),
            values: values,
            idValues: idValues
        };
    }

    function saveAttributeFormDraft() {
        try {
            const draft = collectAttributeFormDraft();
            if (!draft) return;

            sessionStorage.setItem(
                getAttributeDraftStorageKey(),
                JSON.stringify(draft)
            );
        } catch (error) {
            console.warn('Attribute draft could not be saved.', error);
        }
    }

    let attributeDraftSaveTimer = null;

    function scheduleAttributeDraftSave() {
        clearTimeout(attributeDraftSaveTimer);

        attributeDraftSaveTimer = setTimeout(
            saveAttributeFormDraft,
            250
        );
    }

    function ensureAttributeDynamicRowCount(bodyId, requiredCount) {
        const body = document.getElementById(bodyId);
        if (!body || requiredCount === undefined || requiredCount === null) return;

        const templateMap = {
            'body-bus-buildings': busBuildingTemplate,
            'body-bus-sections': busSectionTemplate,
            'body-drc-buildings': drcBuildingTemplate,
            'body-drc-improvements': drcImprovementTemplate,
            'body-drc-vacant': drcVacantTemplate
        };

        const template = templateMap[bodyId];

        while (body.rows.length < requiredCount && template) {
            addRow(bodyId, template);
        }

        while (body.rows.length > requiredCount) {
            body.deleteRow(body.rows.length - 1);
        }

        reIndex();
    }

    function restoreAttributeNamedControl(name, saved) {
        const form = document.getElementById('attributeCreateForm');
        if (!form || !saved) return;

        const controls =
            form.querySelectorAll('[name="' + CSS.escape(name) + '"]');

        if (!controls.length) return;

        if (saved.kind === 'radio') {
            controls.forEach(function (el) {
                if (el.type === 'radio') {
                    el.checked =
                        saved.value !== null &&
                        el.value === saved.value;
                }
            });
            return;
        }

        if (saved.kind === 'checkbox') {
            const checkbox =
                Array.from(controls).find(x => x.type === 'checkbox');

            if (checkbox) checkbox.checked = !!saved.checked;
            return;
        }

        const control =
            Array.from(controls).find(x => x.type !== 'hidden') ||
            controls[0];

        if (!control) return;

        control.value =
            saved.value === null || saved.value === undefined
                ? ''
                : saved.value;
    }

    function refreshAttributeMaskedContacts() {
        document.querySelectorAll('.masked-contact-display').forEach(function (display) {
            const kind = display.dataset.maskKind;
            const index = display.dataset.index;

            const real = document.querySelector(
                '.masked-contact-real[data-mask-kind="' +
                kind +
                '"][data-index="' +
                index +
                '"]'
            );

            if (!real) return;

            display.value =
                kind === 'email'
                    ? maskAttributeEmail(real.value)
                    : maskAttributePhone(real.value);
        });
    }

    function restoreAttributeSignature() {
        const canvas = document.getElementById('sigCanvas');
        const signatureData = document.getElementById('signatureData');
        const status = document.getElementById('sigStatus');

        if (!canvas || !signatureData || !signatureData.value) return;

        const image = new Image();

        image.onload = function () {
            const context = canvas.getContext('2d');

            context.clearRect(0, 0, canvas.width, canvas.height);
            context.drawImage(image, 0, 0, canvas.width, canvas.height);

            if (status) {
                status.textContent = 'Signature restored';
                status.style.color = '#0f766e';
            }
        };

        image.src = signatureData.value;
    }

    function restoreAttributeFormDraft() {
        let draft = null;

        try {
            const raw =
                sessionStorage.getItem(
                    getAttributeDraftStorageKey()
                );

            if (!raw) return;

            draft = JSON.parse(raw);
        } catch (error) {
            console.warn('Attribute draft could not be restored.', error);
            return;
        }

        if (!draft || !draft.savedAt) return;

        if ((Date.now() - draft.savedAt) > ATTRIBUTE_DRAFT_TTL_MS) {
            sessionStorage.removeItem(getAttributeDraftStorageKey());
            return;
        }

        Object.keys(draft.rowCounts || {}).forEach(function (bodyId) {
            ensureAttributeDynamicRowCount(
                bodyId,
                Number(draft.rowCounts[bodyId])
            );
        });

        Object.keys(draft.values || {}).forEach(function (name) {
            restoreAttributeNamedControl(
                name,
                draft.values[name]
            );
        });

        Object.keys(draft.idValues || {}).forEach(function (id) {
            const control = document.getElementById(id);
            const saved = draft.idValues[id];

            if (!control || !saved) return;

            if (saved.kind === 'checkbox') {
                control.checked = !!saved.checked;
            } else {
                control.value =
                    saved.value === null || saved.value === undefined
                        ? ''
                        : saved.value;
            }
        });

        document.getElementById('valuationCategory')
            ?.dispatchEvent(new Event('change', { bubbles: true }));

        document.querySelectorAll('.same-postal-checkbox')
            .forEach(function (checkbox) {
                if (checkbox.checked) {
                    applySameAddress(
                        checkbox.dataset.index,
                        true
                    );
                }
            });

        refreshAttributeMaskedContacts();

        document.querySelectorAll('.attr-email-confirm').forEach(function (confirm) {
            validateAttributeEmailPair(
                confirm.dataset.emailIndex,
                false
            );
        });

        restoreAttributeSignature();
    }

    async function saveAttributeEvidenceDraft() {
        const input = document.getElementById('evFileInput');
        if (!input) return;

        try {
            const db = await openAttributeDraftDb();
            const tx = db.transaction(ATTRIBUTE_FILE_STORE, 'readwrite');
            const store = tx.objectStore(ATTRIBUTE_FILE_STORE);

            const files =
                Array.from(
                    selectedEvidenceFiles?.files ||
                    input.files ||
                    []
                );

            if (!files.length) {
                store.delete(getAttributeEvidenceStorageKey());
            } else {
                store.put({
                    key: getAttributeEvidenceStorageKey(),
                    expiresAt: Date.now() + ATTRIBUTE_DRAFT_TTL_MS,
                    files: files,
                    evidenceTypes:
                        Array.from(selectedEvidenceTypes || [])
                });
            }

            await waitForAttributeTransaction(tx);
            db.close();
        } catch (error) {
            console.warn('Attribute evidence draft could not be saved.', error);
        }
    }

    async function restoreAttributeEvidenceDraft() {
        const input = document.getElementById('evFileInput');
        if (!input) return;

        try {
            const db = await openAttributeDraftDb();
            const tx = db.transaction(ATTRIBUTE_FILE_STORE, 'readwrite');
            const store = tx.objectStore(ATTRIBUTE_FILE_STORE);

            const record =
                await getAttributeDraftRecord(
                    store,
                    getAttributeEvidenceStorageKey()
                );

            if (!record) {
                db.close();
                return;
            }

            if (!record.expiresAt || record.expiresAt < Date.now()) {
                store.delete(getAttributeEvidenceStorageKey());
                await waitForAttributeTransaction(tx);
                db.close();
                return;
            }

            const transfer = new DataTransfer();

            (record.files || []).forEach(function (savedFile) {
                const file =
                    savedFile instanceof File
                        ? savedFile
                        : new File(
                            [savedFile],
                            savedFile.name || 'evidence-file',
                            {
                                type: savedFile.type || '',
                                lastModified:
                                    savedFile.lastModified || Date.now()
                            }
                        );

                transfer.items.add(file);
            });

            selectedEvidenceFiles = transfer;

            selectedEvidenceTypes =
                Array.isArray(record.evidenceTypes)
                    ? record.evidenceTypes.slice(0, transfer.files.length)
                    : [];

            while (selectedEvidenceTypes.length < transfer.files.length) {
                selectedEvidenceTypes.push('Supporting Document');
            }

            input.files = selectedEvidenceFiles.files;

            syncEvidenceTypeInputs();

            updateEvidenceList(
                selectedEvidenceFiles.files,
                document.getElementById('evFileList'),
                document.getElementById('evCountText'),
                document.getElementById('evCountFill')
            );

            updateEvSummaryBadge();
            db.close();
        } catch (error) {
            console.warn('Attribute evidence draft could not be restored.', error);
        }
    }

    async function saveAttributeRepLetterDraft() {
        const input = document.getElementById('evRepLetterInput');
        if (!input) return;

        try {
            const db = await openAttributeDraftDb();
            const tx = db.transaction(ATTRIBUTE_FILE_STORE, 'readwrite');
            const store = tx.objectStore(ATTRIBUTE_FILE_STORE);

            const file =
                input.files && input.files.length
                    ? input.files[0]
                    : null;

            if (!file) {
                store.delete(getAttributeRepLetterStorageKey());
            } else {
                store.put({
                    key: getAttributeRepLetterStorageKey(),
                    expiresAt: Date.now() + ATTRIBUTE_DRAFT_TTL_MS,
                    file: file
                });
            }

            await waitForAttributeTransaction(tx);
            db.close();
        } catch (error) {
            console.warn('Authorisation letter draft could not be saved.', error);
        }
    }

    async function restoreAttributeRepLetterDraft() {
        const input = document.getElementById('evRepLetterInput');
        if (!input) return;

        try {
            const db = await openAttributeDraftDb();
            const tx = db.transaction(ATTRIBUTE_FILE_STORE, 'readwrite');
            const store = tx.objectStore(ATTRIBUTE_FILE_STORE);

            const record =
                await getAttributeDraftRecord(
                    store,
                    getAttributeRepLetterStorageKey()
                );

            if (!record) {
                db.close();
                return;
            }

            if (!record.expiresAt || record.expiresAt < Date.now()) {
                store.delete(getAttributeRepLetterStorageKey());
                await waitForAttributeTransaction(tx);
                db.close();
                return;
            }

            if (record.file) {
                const transfer = new DataTransfer();

                const restoredFile =
                    record.file instanceof File
                        ? record.file
                        : new File(
                            [record.file],
                            record.file.name || 'authorisation-letter',
                            {
                                type: record.file.type || '',
                                lastModified:
                                    record.file.lastModified || Date.now()
                            }
                        );

                transfer.items.add(restoredFile);
                input.files = transfer.files;

                input.dispatchEvent(
                    new Event('change', { bubbles: true })
                );
            }

            db.close();
        } catch (error) {
            console.warn('Authorisation letter draft could not be restored.', error);
        }
    }

    async function clearAttributeDraft() {
        try {
            sessionStorage.removeItem(getAttributeDraftStorageKey());
        } catch (_) { }

        try {
            const db = await openAttributeDraftDb();
            const tx = db.transaction(ATTRIBUTE_FILE_STORE, 'readwrite');
            const store = tx.objectStore(ATTRIBUTE_FILE_STORE);

            store.delete(getAttributeEvidenceStorageKey());
            store.delete(getAttributeRepLetterStorageKey());

            await waitForAttributeTransaction(tx);
            db.close();
        } catch (error) {
            console.warn('Attribute draft files could not be cleared.', error);
        }
    }

    function initialiseAttributeDraftRecovery() {
        const form = document.getElementById('attributeCreateForm');
        if (!form) return;

        restoreAttributeFormDraft();
        restoreAttributeEvidenceDraft();
        restoreAttributeRepLetterDraft();

        form.addEventListener('input', scheduleAttributeDraftSave);
        form.addEventListener('change', scheduleAttributeDraftSave);

        document.getElementById('evFileInput')
            ?.addEventListener('change', function () {
                // Wait until the existing upload logic has rebuilt
                // selectedEvidenceFiles and selectedEvidenceTypes.
                setTimeout(saveAttributeEvidenceDraft, 0);
            });

        document.getElementById('evRepLetterInput')
            ?.addEventListener('change', saveAttributeRepLetterDraft);

        const resetLink = document.getElementById('resetAttributeForm');

        resetLink?.addEventListener('click', async function (event) {
            event.preventDefault();

            const destination = resetLink.href;
            await clearAttributeDraft();

            window.location.href = destination;
        });

        window.addEventListener('pagehide', function () {
            saveAttributeFormDraft();
            saveAttributeEvidenceDraft();
            saveAttributeRepLetterDraft();
        });
    }

    // Call this only after the server has confirmed a successful submission.
    window.clearGenesisAttributeCreateDraft = clearAttributeDraft;


            // Signature pad. The Base64 PNG is posted through
    // Declaration.SignaturePicture.
    // ─────────────────────────────────────────────
    function initialiseSignaturePad() {
        const canvas = document.getElementById('sigCanvas');
        const signatureData = document.getElementById('signatureData');
        const status = document.getElementById('sigStatus');

        if (!canvas || !signatureData) return;

        const context = canvas.getContext('2d');
        let drawing = false;
        let hasSignature = false;

        context.lineWidth = 2.5;
        context.lineCap = 'round';
        context.lineJoin = 'round';
        context.strokeStyle = '#123b42';

        function pointFromEvent(event) {
            const rect = canvas.getBoundingClientRect();
            return {
                x: (event.clientX - rect.left) * (canvas.width / rect.width),
                y: (event.clientY - rect.top) * (canvas.height / rect.height)
            };
        }

        function startDrawing(event) {
            event.preventDefault();
            drawing = true;
            const point = pointFromEvent(event);
            context.beginPath();
            context.moveTo(point.x, point.y);
            if (canvas.setPointerCapture) canvas.setPointerCapture(event.pointerId);
        }

        function draw(event) {
            if (!drawing) return;
            event.preventDefault();
            const point = pointFromEvent(event);
            context.lineTo(point.x, point.y);
            context.stroke();
            hasSignature = true;
            if (status) {
                status.textContent = 'Signature captured';
                status.style.color = '#0f766e';
            }
        }

        function stopDrawing(event) {
            if (!drawing) return;
            drawing = false;
            context.closePath();
            if (event && canvas.releasePointerCapture &&
                canvas.hasPointerCapture(event.pointerId)) {
                canvas.releasePointerCapture(event.pointerId);
            }
            if (hasSignature) {
                signatureData.value = canvas.toDataURL('image/png');
                signatureData.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }

        canvas.style.touchAction = 'none';
        canvas.addEventListener('pointerdown', startDrawing);
        canvas.addEventListener('pointermove', draw);
        canvas.addEventListener('pointerup', stopDrawing);
        canvas.addEventListener('pointercancel', stopDrawing);
        canvas.addEventListener('pointerleave', stopDrawing);

        window.clearSignature = function () {
            context.clearRect(0, 0, canvas.width, canvas.height);
            signatureData.value = '';
            hasSignature = false;
            if (status) {
                status.textContent = 'No signature drawn';
                status.style.color = '#6b6b6b';
            }
        };
    }

    // ─────────────────────────────────────────────
    // Final submit validation
    // ─────────────────────────────────────────────
    function initialiseSubmitValidation() {
        const form = document.getElementById('attributeCreateForm');

        if (!form) return;

        form.addEventListener('submit', function (e) {
            // Keep the draft until the server has accepted the submission.
            saveAttributeFormDraft();
            saveAttributeEvidenceDraft();
            saveAttributeRepLetterDraft();

            if (!validateAllAttributeEmailPairs(true)) {
                e.preventDefault();

                const invalidEmail =
                    document.querySelector(
                        '.attr-email-confirm.is-invalid-email, .attr-email-primary.is-invalid-email'
                    );

                invalidEmail?.scrollIntoView({
                    behavior: 'smooth',
                    block: 'center'
                });

                invalidEmail?.focus();

                alert(
                    'Please make sure the email address and confirmation email match before submitting.'
                );

                return;
            }

            const badCell = validateAttributeCellNumbers(true);
            if (badCell) {
                e.preventDefault();
                badCell.scrollIntoView({ behavior: 'smooth', block: 'center' });
                badCell.focus();
                alert('Please enter a valid cell number: it must start with 0 (e.g. 0671234567) or +27 (e.g. +27671234567).');
                return;
            }

            const category = document.getElementById('valuationCategory');
            const mixedSelect = document.getElementById('mixedUseSelect');
            const signatureData = document.getElementById('signatureData');
            const declarationAccepted = document.getElementById('declAccepted');
            const evidenceInput = document.getElementById('evFileInput');
            const evidenceType = document.getElementById('evEvidenceType');

            if (evidenceInput &&
                evidenceInput.files &&
                evidenceInput.files.length > 0 &&
                selectedEvidenceTypes.length !== evidenceInput.files.length) {
                e.preventDefault();
                alert('Please select an evidence type for every uploaded file.');
                evidenceType?.focus();
                return;
            }

            const selectedCategory = category ? (category.value || '').trim().toLowerCase() : '';

            const isMulti =
                selectedCategory === 'multipurpose' ||
                selectedCategory === 'multiple purposes' ||
                selectedCategory === 'multi purpose' ||
                selectedCategory === 'multi-purpose';

            if (isMulti && mixedSelect && !mixedSelect.value) {
                e.preventDefault();
                alert('Please select the mixed-use type.');
                mixedSelect.focus();
                return;
            }

            if (declarationAccepted && !declarationAccepted.checked) {
                e.preventDefault();
                alert('Please accept the declaration before submitting.');
                declarationAccepted.focus();
                return;
            }

            if (signatureData && !signatureData.value) {
                e.preventDefault();
                alert('Please draw your signature before submitting.');
                return;
            }

            if (evidenceInput && evidenceInput.files) {
                const maxFiles = 10;
                const maxFileSizeMb = 20;
                const allowedExtensions = ['pdf', 'xls', 'xlsx', 'jpg', 'jpeg', 'png'];

                if (evidenceInput.files.length > maxFiles) {
                    e.preventDefault();
                    alert('You can upload a maximum of 10 supporting documents.');
                    evidenceInput.focus();
                    return;
                }

                for (const file of evidenceInput.files) {
                    const sizeMb = file.size / 1024 / 1024;
                    const ext = (file.name.split('.').pop() || '').toLowerCase();

                    if (!allowedExtensions.includes(ext)) {
                        e.preventDefault();
                        alert(file.name + ' is not a supported file type.');
                        evidenceInput.focus();
                        return;
                    }

                    if (sizeMb > maxFileSizeMb) {
                        e.preventDefault();
                        alert(file.name + ' is larger than 20 MB.');
                        evidenceInput.focus();
                        return;
                    }
                }
            }

            
        });
    }
