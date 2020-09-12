﻿using System;
using System.Numerics;

namespace Ryujinx.Motion
{
    // MahonyAHRS class. Madgwick's implementation of Mayhony's AHRS algorithm.
    // See: https://x-io.co.uk/open-source-imu-and-ahrs-algorithms/
    // Based on: https://github.com/xioTechnologies/Open-Source-AHRS-With-x-IMU/blob/master/x-IMU%20IMU%20and%20AHRS%20Algorithms/x-IMU%20IMU%20and%20AHRS%20Algorithms/AHRS/MahonyAHRS.cs
    public class MotionSensorFilter
    {
        /// <summary>
        /// Sample rate coefficient.
        /// </summary>
        public const float SampleRateCoefficient = 0.45f;
        private bool _initilized;

        /// <summary>
        /// Gets or sets the sample period.
        /// </summary>
        public float SamplePeriod { get; set; }

        /// <summary>
        /// Gets or sets the algorithm proportional gain.
        /// </summary>
        public float Kp { get; set; }

        /// <summary>
        /// Gets or sets the algorithm integral gain.
        /// </summary>
        public float Ki { get; set; }

        /// <summary>
        /// Quaternion output.
        /// </summary>
        public Quaternion Quaternion;
        
        
        /// <summary>
        /// Reference orientation.
        /// </summary>
        private Quaternion _referenceOrientation;

        /// <summary>
        /// Integral error.
        /// </summary>
        private Vector3 _intergralError;

        /// <summary>
        /// Initializes a new instance of the <see cref="MotionSensorFilter"/> class.
        /// </summary>
        /// <param name="samplePeriod">
        /// Sample period.
        /// </param>
        public MotionSensorFilter(float samplePeriod) : this(samplePeriod, 1f, 0f)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MotionSensorFilter"/> class.
        /// </summary>
        /// <param name="samplePeriod">
        /// Sample period.
        /// </param>
        /// <param name="kp">
        /// Algorithm proportional gain.
        /// </param>
        public MotionSensorFilter(float samplePeriod, float kp) : this(samplePeriod, kp, 0f)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MotionSensorFilter"/> class.
        /// </summary>
        /// <param name="samplePeriod">
        /// Sample period.
        /// </param>
        /// <param name="kp">
        /// Algorithm proportional gain.
        /// </param>
        /// <param name="ki">
        /// Algorithm integral gain.
        /// </param>
        public MotionSensorFilter(float samplePeriod, float kp, float ki)
        {
            SamplePeriod = samplePeriod;
            Kp = kp;
            Ki = ki;
            Quaternion = Quaternion.Identity;
            _intergralError = new Vector3();

            Update(new Vector3(0, 0, -1), default);

            _referenceOrientation = Quaternion;

            _initilized = true;
        }

        public void Reset()
        {
             _referenceOrientation = Quaternion;
        }

        public Quaternion Orientation
        {
            get
            {
                return Quaternion * Quaternion.Inverse(_referenceOrientation);
            }
        }

        /// <summary>
        /// Algorithm IMU update method. Requires only gyroscope and accelerometer data.
        /// </summary>
        /// <param name="accel">
        /// Accelerometer measurement in any calibrated units.
        /// </param>
        /// <param name="gyro">
        /// Gyroscope measurement in radians.
        /// </param>
        public void Update(Vector3 accel, Vector3 gyro)
        {
            // Normalise accelerometer measurement.
            float norm = 1f / accel.Length();
            if (!float.IsFinite(norm))
            {
                return;
            }

            accel *= norm;

            float q1 = Quaternion.W;
            float q2 = Quaternion.X;
            float q3 = Quaternion.Y;
            float q4 = Quaternion.Z;

            // Estimated direction of gravity.
            Vector3 gravity = new Vector3();
            gravity.X = 2f * (q2 * q4 - q1 * q3);
            gravity.Y = 2f * (q1 * q2 + q3 * q4);
            gravity.Z = q1 * q1 - q2 * q2 - q3 * q3 + q4 * q4;

            // Error is cross product between estimated direction and measured direction of gravity.
            Vector3 error = new Vector3();
            error.X = accel.Y * gravity.Z - accel.Z * gravity.Y;
            error.Y = accel.Z * gravity.X - accel.X * gravity.Z;
            error.Z = accel.X * gravity.Y - accel.Y * gravity.X;

            if (Ki > 0f)
            {
                _intergralError += error; // Accumulate integral error.
            }
            else
            {
                _intergralError = new Vector3(); // Prevent integral wind up.
            }

            // Apply feedback terms.
            gyro += (Kp * error) + (Ki * _intergralError);

            // Integrate rate of change of quaternion.
            Vector3 delta = new Vector3(q2, q3, q4);

            q1 += (-q2 * gyro.X - q3 * gyro.Y - q4 * gyro.Z) * (SampleRateCoefficient * SamplePeriod);
            q2 += (q1 * gyro.X + delta.Y * gyro.Z - delta.Z * gyro.Y) * (SampleRateCoefficient * SamplePeriod);
            q3 += (q1 * gyro.Y - delta.X * gyro.Z + delta.Z * gyro.X) * (SampleRateCoefficient * SamplePeriod);
            q4 += (q1 * gyro.Z + delta.X * gyro.Y - delta.Y * gyro.X) * (SampleRateCoefficient * SamplePeriod);

            // Normalise quaternion.
            norm = Quaternion.Length();
            if (norm == 0f || float.IsNaN(norm))
            {
                return; // Handle NaNs.
            }
            norm = 1f / norm;

            Quaternion.W = q1 * norm;
            Quaternion.X = q2 * norm;
            Quaternion.Y = q3 * norm;
            Quaternion.Z = q4 * norm;
        }
    }
}
