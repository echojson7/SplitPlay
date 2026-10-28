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
  <ModalDialog :open="open" :title="title" :width="400" @close="emit('close')">
    <div class="confirm">
      <slot />
      <div class="actions">
        <button type="button" class="btn-ghost" @click="emit('close')">{{ t('common.cancel') }}</button>
        <button
          type="button"
          :class="[danger ? 'btn-danger-solid' : 'btn-primary', 'primary-action']"
          :disabled="busy"
          data-confirm
          @click="emit('confirm')"
        >
          {{ confirmLabel }}
        </button>
      </div>
    </div>
  </ModalDialog>
</template>

<style scoped>
.confirm {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

.actions button {
  min-width: 110px;
}
</style>
