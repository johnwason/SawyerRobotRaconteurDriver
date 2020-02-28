﻿using System;
using System.Collections.Generic;
using System.Text;
using RobotRaconteurWeb;
using com.robotraconteur.robotics.robot;
using System.IO;
using System.Linq;
using ros_csharp_interop;
using com.robotraconteur.robotics.joints;
using ros_csharp_interop.rosmsg.gen.intera_core_msgs;
using ros_csharp_interop.rosmsg.gen.std_msgs;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using com.robotraconteur.geometry;
using com.robotraconteur.action;
using com.robotraconteur.robotics.trajectory;
using RobotRaconteurWeb.Robot;

namespace SawyerRobotRaconteurDriver
{
    public class SawyerRobot : AbstractRobot, IDisposable
    {
        protected ROSNode _ros_node;
        protected string _ros_ns_prefix;
                
        protected Subscriber<ros_csharp_interop.rosmsg.gen.sensor_msgs.JointState> _joint_states_sub;
        protected Subscriber<RobotAssemblyState> _robot_state_sub;
        protected Subscriber<EndpointState> _endpoint_state_sub;
        protected Publisher<JointCommand> _joint_command_pub;
        protected Publisher<Empty> _set_super_reset_pub;
        protected Publisher<Empty> _set_super_stop_pub;
        protected Publisher<Bool> _set_super_enable_pub;
        protected internal Publisher<HomingCommand> _homing_command_pub;

        protected Publisher<DigitalOutputCommand> _digital_io_pub;
        protected List<Subscriber<DigitalIOState>> _digital_io_sub = new List<Subscriber<DigitalIOState>>();
        protected Dictionary<string, bool> _digital_io_states = new Dictionary<string, bool>();

        protected string[] _digital_io_names = { "right_valve_1a", "right_valve_1b", "right_valve_2a", "right_valve_2b" };


        public SawyerRobot(com.robotraconteur.robotics.robot.RobotInfo robot_info, string ros_ns_prefix = "") : base(robot_info, 7)
        {
            this._ros_ns_prefix = "";
            if (robot_info.joint_info == null)
            {
                _joint_names = Enumerable.Range(0, 7).Select(x => $"right_j{x}").ToArray();
            }
        }

        

        public override void _start_robot()
        {  
            _ros_node = new ROSNode();
            _joint_states_sub = _ros_node.subscribe<ros_csharp_interop.rosmsg.gen.sensor_msgs.JointState>(_ros_ns_prefix + "robot/joint_states", 1, _joint_state_cb); ;
            _robot_state_sub = _ros_node.subscribe<RobotAssemblyState>(_ros_ns_prefix + "robot/state", 1, _robot_state_cb); ;
            _endpoint_state_sub = _ros_node.subscribe<EndpointState>(_ros_ns_prefix + "robot/limb/right/endpoint_state", 1, _endpoint_state_cb); ;
            _joint_command_pub = _ros_node.advertise<JointCommand>(_ros_ns_prefix + "robot/limb/right/joint_command", 1, false);
            _set_super_reset_pub = _ros_node.advertise<Empty>(_ros_ns_prefix + "robot/set_super_reset", 1, false);
            _set_super_stop_pub = _ros_node.advertise<Empty>(_ros_ns_prefix + "robot/set_super_stop", 1, false);
            _set_super_enable_pub = _ros_node.advertise<Bool>(_ros_ns_prefix + "robot/set_super_enable", 1, false);
            _homing_command_pub = _ros_node.advertise<HomingCommand>(_ros_ns_prefix + "robot/set_homing_mode", 1, false);
            _digital_io_pub = _ros_node.advertise<DigitalOutputCommand>(_ros_ns_prefix + "robot/digital_io/command", 10, false);

            foreach (var d in _digital_io_names)
            {
                var sub = _ros_node.subscribe<DigitalIOState>(_ros_ns_prefix + $"robot/digitial_io/{d}/state", 1, msg => _digitital_io_state_cb(d, msg));
            }

            base._start_robot();

            _ros_node.start_spinner();            
        }

        

        protected internal void _robot_state_cb(RobotAssemblyState msg)
        {
            lock(this)
            {
                _last_robot_state = _stopwatch.ElapsedMilliseconds;

                _homed = msg.homed;
                _ready = msg.ready;
                _enabled = msg.enabled;
                _stopped = msg.stopped;
                _error = msg.error;
                _estop_source = msg.estop_source;
            }
        }

        protected internal void _endpoint_state_cb(EndpointState msg)
        {
            lock (this)
            {
                _last_endpoint_state = _stopwatch.ElapsedMilliseconds;

                if (!msg.valid)
                {
                    _endpoint_pose = null;
                    _endpoint_vel = null;
                    return;
                }

                var p = new Pose();
                p.orientation.w = msg.pose.orientation.w;
                p.orientation.x = msg.pose.orientation.x;
                p.orientation.y = msg.pose.orientation.y;
                p.orientation.z = msg.pose.orientation.z;
                p.position.x = msg.pose.position.x;
                p.position.y = msg.pose.position.y;
                p.position.z = msg.pose.position.z;

                var v = new SpatialVelocity();
                v.angular.x = msg.twist.angular.x;
                v.angular.y = msg.twist.angular.y;
                v.angular.z = msg.twist.angular.z;
                v.linear.x = msg.twist.linear.x;
                v.linear.y = msg.twist.linear.y;
                v.linear.z = msg.twist.linear.z;

                _endpoint_pose = p;
                _endpoint_vel = v;
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _joint_states_sub?.Dispose();
            _robot_state_sub?.Dispose();
            _endpoint_state_sub?.Dispose();
            _joint_command_pub?.Dispose();
            _set_super_reset_pub?.Dispose();
            _set_super_stop_pub?.Dispose();
            _set_super_enable_pub?.Dispose();
            _homing_command_pub?.Dispose();
            _digital_io_pub?.Dispose();
            foreach (var d in _digital_io_sub)
            {
                d?.Dispose();
            }

            _ros_node?.Dispose();
        }

        protected internal void _joint_state_cb(ros_csharp_interop.rosmsg.gen.sensor_msgs.JointState joint_states)
        {
            if (joint_states.name.Length < _joint_count)
            {
                return;
            }

            var joint_ind = new int[_joint_count];

            int last_ind = -1;
            for (int i=0; i<_joint_count; i++)
            {
                int last_ind_1 = last_ind + 1;
                if (last_ind_1 < joint_states.name.Length && joint_states.name[last_ind_1] == _joint_names[i])
                {
                    joint_ind[i] = last_ind_1;
                    last_ind = last_ind_1;
                    continue;
                }
                else
                {
                    bool joint_ind_found = false;
                    for (int j=0; j<joint_states.name.Length; j++)
                    {
                        if (joint_states.name[j] == _joint_names[i])
                        {
                            joint_ind_found = true;
                            joint_ind[i] = j;
                            last_ind = j;
                            break;
                        }
                    }

                    if (!joint_ind_found)
                    {
                        // We didn't find the joint name...
                        return;
                    }
                }
            }

            lock (this)
            {
                _last_joint_state = _stopwatch.ElapsedMilliseconds;

                if (joint_states.position.Length == joint_states.name.Length)
                {
                    if (_joint_position == null || _joint_position.Length != _joint_count) _joint_position = new double[_joint_count];
                    for (int i = 0; i < _joint_count; i++) _joint_position[i] = joint_states.position[joint_ind[i]];
                }
                else
                {
                    _joint_position = null;
                }

                if (joint_states.velocity.Length == joint_states.name.Length)
                {
                    if (_joint_velocity == null || _joint_velocity.Length != _joint_count) _joint_velocity = new double[_joint_count];
                    for (int i = 0; i < _joint_count; i++) _joint_velocity[i] = joint_states.velocity[joint_ind[i]];
                }
                else
                {
                    _joint_velocity = null;
                }

                if (joint_states.effort.Length == joint_states.name.Length)
                {
                    if (_joint_effort == null || _joint_effort.Length != _joint_count) _joint_effort = new double[_joint_count];
                    for (int i = 0; i < _joint_count; i++) _joint_effort[i] = joint_states.effort[joint_ind[i]];
                }
                else
                {
                    _joint_effort = null;
                }
            }                    
        }

        protected void _digitital_io_state_cb(string name, DigitalIOState state)
        {
            lock(this)
            {
                _digital_io_states[name] = state.state == 1;
            }
        }

        
        protected override Task _send_disable()
        {
            var msg = new Bool();
            msg.data = false;
            _set_super_enable_pub.publish(msg);
            return Task.FromResult(0);
        }

       
        protected override Task _send_enable()
        {
            var msg = new Bool();
            msg.data = true;
            _set_super_enable_pub.publish(msg);

            return Task.FromResult(0);
        }

        protected override Task _send_reset_errors()
        {
            var msg = new Empty();            
            _set_super_reset_pub.publish(msg);

            return Task.FromResult(0);
        }


        protected override void _send_robot_command(long now, double[] joint_pos_cmd, double[] joint_vel_cmd)
        {
            if (joint_pos_cmd != null)
            {
                var msg = new JointCommand();
                msg.header = new Header();
                msg.mode = 1;
                msg.names = _joint_names;
                msg.position = joint_pos_cmd;
                _joint_command_pub.publish(msg);
                return;
            }

            if (joint_vel_cmd != null)
            {
                var msg = new JointCommand();
                msg.header = new Header();
                msg.mode = 2;
                msg.names = _joint_names;
                msg.velocity = joint_vel_cmd;
                _joint_command_pub.publish(msg);
                return;
            }
        }
        
                
        public override Task<Generator2<com.robotraconteur.action.ActionStatusCode>> home(CancellationToken rr_cancel = default)
        {
            lock(this)
            {
                if (!_enabled || _error || _communication_failure)
                {
                    throw new InvalidOperationException("Robot must be communicating and enabled to home");
                }

                var homing_task = new SawyerHomingTask(this);
                return Task.FromResult<Generator2<com.robotraconteur.action.ActionStatusCode>>(homing_task);
            }
        }

        public override Task<double[]> getf_signal(string signal_name, CancellationToken rr_cancel = default)
        {
            lock (this)
            {
                if (_digital_io_states.TryGetValue(signal_name, out var state))
                {
                    if (state)
                    {
                        return Task.FromResult(new double[] { 1.0 });
                    }
                    else
                    {
                        return Task.FromResult(new double[] { 0.0 });
                    }
                }
            }

            if (_digital_io_names.Contains(signal_name))
            {
                throw new ValueNotSetException("Signal value not read");
            }

            throw new ArgumentException("Invalid signal name");            
        }

        public override Task setf_signal(string signal_name, double[] value_, CancellationToken rr_cancel = default)
        {
            if (_digital_io_names.Contains(signal_name))
            {
                if (value_.Length != 1)
                {
                    throw new ArgumentException("Expected single element array for binary signal");
                }

                if (value_[0] != 0 && value_[0] != 1)
                {
                    throw new ArgumentException("Expected 0 or 1 for binary signal");
                }

                var msg = new DigitalOutputCommand();
                msg.name = signal_name;
                msg.value = value_[0] != 1.0;
                _digital_io_pub.publish(msg);
                return Task.FromResult(0);
            }

            throw new ArgumentException("Unknown signal_name");
        }

        internal int _sawyer_joint_count => _joint_count;
        internal string[] _sawyer_joint_names => _joint_names;
        internal bool _sawyer_homed => _homed;
        internal bool _sawyer_enabled => _enabled;
        internal RobotCommandMode _sawyer_command_mode => _command_mode;
        internal void _sawyer_send_disable()
        {
            _send_disable().ContinueWith(t => { });
        }
    }

    class SawyerHomingTask : Generator2<com.robotraconteur.action.ActionStatusCode>
    {

        SawyerRobot parent;
        bool started = false;
        bool next_called = false;
        long start_time;
        bool done = false;

        internal SawyerHomingTask(SawyerRobot parent)
        {
            this.parent = parent;
        }

        public Task Abort(CancellationToken cancel = default)
        {
            parent._sawyer_send_disable();
            return Task.FromResult(0);
        }

        public async Task Close(CancellationToken cancel = default)
        {
            await parent.halt();
        }

        public async Task<ActionStatusCode> Next(CancellationToken cancel = default)
        {
            lock(this)
            {
                if (done)
                {
                    throw new StopIterationException("");
                }

                if (next_called)
                {
                    throw new MemberBusyException("Next has already been called");
                }
                next_called = true;
            }
            try
            {
                long func_start_time = parent._now;
                int send_downsample = 0;
                while (!_robot_homed)
                {
                    if (!started)
                    {
                        start_time = func_start_time;
                        started = true;

                        var homing_command = new HomingCommand();
                        homing_command.name = parent._sawyer_joint_names;
                        homing_command.command = Enumerable.Repeat(0, parent._sawyer_joint_count).ToArray();
                        parent._homing_command_pub.publish(homing_command);
                        await Task.Delay(500);
                    }

                    if (send_downsample > 10) send_downsample = 0;
                    if (send_downsample == 0)
                    {
                        var homing_command = new HomingCommand();
                        homing_command.name = parent._sawyer_joint_names;
                        homing_command.command = Enumerable.Repeat(2, parent._sawyer_joint_count).ToArray();
                        parent._homing_command_pub.publish(homing_command);
                    }

                    await Task.Delay(100);

                    var now = parent._now;

                    if (now - start_time > 30000)
                    {
                        throw new TimeoutException("Homing timed out");
                    }

                    if (now - func_start_time > 5000)
                    {
                        return ActionStatusCode.running;
                    }                    
                }

                done = true;
                return ActionStatusCode.complete;
            }
            catch (Exception)
            {
                done = true;
                throw;
            }
            finally
            {
                next_called = false;
            }
        }

        public Task<ActionStatusCode[]> NextAll(CancellationToken cancel = default)
        {
            // Not called on server
            throw new NotImplementedException();
        }

        protected internal bool _robot_homed
        {
            get
            {
                lock(parent)
                {
                    if (parent._sawyer_homed)
                    {
                        return true;
                    }
                    if (parent._sawyer_command_mode != RobotCommandMode.homing || parent._sawyer_enabled != true)
                    {
                        throw new OperationFailedException("Homing failed");
                    }
                    return false;
                }
            }
        }
    }
}
