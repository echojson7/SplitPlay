<!-- SPDX-License-Identifier: MIT -->
<!-- Four-digit PIN entry: a plain text box that keeps digits only. -->
<script setup lang="ts">
import { ref } from 'vue'

withDefaults(defineProps<{ modelValue: string; id?: string; disabled?: boolean; invalid?: boolean; label?: string }>(), {
  id: 'pin',
  disabled: false,
  invalid: false,
  label: 'PIN',
})
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const input = ref<HTMLInputElement | null>(null)

function onInput(e: Event): void {
  const el = e.target as HTMLInputElement
  const clean = el.value.replace(/\D/g, '').slice(0, 4)
  // Keep the DOM in step even when the model value does not change (letters typed).
  el.value = clean
  emit('update:modelValue', clean)
}

defineExpose({ focus: () => input.value?.focus() })
</script>

<template>
  <input
    :id="id"
    ref="input"
    class="pin"
    name="pin"
    inputmode="numeric"
    autocomplete="one-time-code"
    maxlength="4"
    spellcheck="false"
    placeholder="0000"
    :aria-label="label"
    :aria-invalid="invalid"
    :disabled="disabled"
    :value="modelValue"
    @input="onInput"
  />
</template>

<style scoped>
.pin {
  width: 96px;
  font-variant-numeric: tabular-nums;
}
</style>
