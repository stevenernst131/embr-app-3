if (process.env.HUNT_FAIL_BUILD === '1') {
  throw new Error('HUNT_CONTROLLED_BUILD_FAILURE');
}
console.log('Validation build passed');
