<!-- SPDX-License-Identifier: MIT -->
<!-- Yes/no confirmation in a modal; extra controls (a checkbox) go in the default slot. -->
<script setup lang="ts">
import { t } from '../i18n'
import ModalDialog from './ModalDialog.vue'

withDefaults(defineProps<{ open: boolean; title: string; confirmLabel: string; danger?: boolean; busy?: boolean }>(), {
  danger: true,
  busy: false,
})
const emit = defineEmits<{ confirm: []; close: [] }>()
</script>

<template>
  <ModalDialog :open="open" :title="title" :width="440" @close="emit('close')">
    <div class="confirm">
      <slot />
      <div class="dlg-foot">
        <button type="button" class="btn-accent primary-action" :disabled="busy" data-confirm @click="emit('confirm')">
          {{ confirmLabel }}
        </button>
        <button type="button" @click="emit('close')">{{ t('common.cancel') }}</button>
      </div>
    </div>
  </ModalDialog>
</template>

<style scoped>
.confirm {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.dlg-foot > button {
  flex: 1;
}
</style>
